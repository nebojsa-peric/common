﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.SqlServer.Management.Smo;
using Codaxy.Common.Logging;
using System.Data.SqlClient;
using Microsoft.SqlServer.Management.Common;
using System.IO;

namespace Codaxy.Common.SqlServer
{
    public class SqlSchemaUpgradeManager : SqlSchemaManager
    {
		public SqlSchemaUpgradeManager()
		{
			GetVersionSqlCommandText = "SELECT [Current] FROM [Version]";
			SetVersionSqlCommandText = "UPDATE [Version] SET [Current]='{Version}'";
			ScriptVersionNumberGetter = DefaultScriptVersionNumberGetter;
		}

		/// <summary>
		/// Database backup directory.
		/// </summary>
        public String BackupLocation { get; set; }

		/// <summary>
		/// Set to true to backup database before upgrade is performed. If upgrde fails database will be restored from the backup.
		/// </summary>
        public bool UpgradeBackupAndRestoreOnError { get; set; }

		/// <summary>
		/// Extract database version from script name. By default first 4 letters are used and must be digits.
		/// </summary>
        public Func<String, String> ScriptVersionNumberGetter { get; set; }        
		
		/// <summary>
		/// Logger for the upgrade.
		/// </summary>
		public Logger Logger { get; set; }

		/// <summary>
		/// SQL command used to retrieve current version of database schema. 
		/// Default: SELECT [Current] FROM [Version]
		/// </summary>
        public String GetVersionSqlCommandText { get; set; }

		/// <summary>
		/// SQL command to set new version number
		/// Default: UPDATE [Version] SET [Current]='{Version}';
		/// </summary>
        public String SetVersionSqlCommandText { get; set; }

        class ScriptInfo
        {
            public String Version { get; set; }
            public SqlScript Script { get; set; }
        }

        protected override void Validate()
        {
            base.Validate();

            if (ScriptVersionNumberGetter == null)
                throw new InvalidDatabaseManagerSettingsException("ScriptsVersionNumberGetter");

            if (GetVersionSqlCommandText==null)
                throw new InvalidDatabaseManagerSettingsException("GetVersionSqlCommandText");

            if (SetVersionSqlCommandText == null)
                throw new InvalidDatabaseManagerSettingsException("SetVersionSqlCommandText");
        }

        public bool UpgradeSchema(SqlScript[] scripts)
        {
            Validate();
            if (scripts.Length == 0)
                return false;
            bool upgradeStarted = false;

            string databaseName;
            var server = GetServer(out databaseName);
            var db = server.Databases[databaseName];
            if (db == null)
                throw new InvalidDatabaseOperationException(String.Format("Database '{0}' not found on the server.", databaseName));


            String currentVersion;
            try
            {
                var ds = db.ExecuteWithResults(GetVersionSqlCommandText);
                currentVersion = ds.Tables[0].Rows[0][0].ToString();
            }
            catch (Exception ex)
            {
                throw new InvalidDatabaseOperationException("Could not determine current version of the database. Check get version command.", ex);
            }

			List<ScriptInfo> sortedScripts = new List<ScriptInfo>();
			foreach (var script in scripts)
				try
				{
					sortedScripts.Add(new ScriptInfo
					{
						Version = ScriptVersionNumberGetter(script.Name),
						Script = script
					});
				}
				catch (Exception ex)
				{
					throw new InvalidDatabaseOperationException(String.Format("Could not determine upgrade script '{0}' version.", script.Name), ex);
				}

			sortedScripts = sortedScripts.OrderBy(a => a.Version).ThenBy(a => a.Script.Name).ToList();
			var targetVersion = sortedScripts.Last().Version;
            if (String.Compare(currentVersion, targetVersion) >= 0)
            {
                Logger.InfoFormat("Database schema is up to date. (Version: '{0}')", currentVersion);
                return false;
            }

            try
            {
                PrepareForUpgrade(server, databaseName, currentVersion);
                upgradeStarted = true;
            }
            catch (Exception ex)
            {
                throw new InvalidDatabaseOperationException("Could not make database backup. Check inner exception for more details.", ex);
            }

            try
            {
                foreach (var script in sortedScripts)
                    if (String.Compare(script.Version, currentVersion) > 0)
                        try
                        {
                            db.ExecuteNonQuery(script.Script.SQL);
                        }
                        catch (Exception ex)
                        {
                            throw new ExecuteDatabaseScriptException("Database script '{0}' failed. Check inner exception for more details.", ex, script.Script);
                        }

                try
                {
                    db.ExecuteNonQuery(SetVersionSqlCommandText.Replace("{Version}", targetVersion));
                }
                catch (Exception ex)
                {
                    throw new InvalidDatabaseOperationException("Setting the new database version failed. Check set version command and inner exception for more details.", ex);
                }
                Logger.InfoFormat("Database has been successfully upgraded to version '{0}'.", targetVersion);
                FreeUpgradeResources();
                return true;
            }
            catch
            {
                try
                {
                    if (upgradeStarted)
                        try
                        {
                            RollbackUpgrade(server, databaseName);
                            Logger.Error("Database restored to original version.");
                            FreeUpgradeResources();
                        }
                        catch (Exception rbex)
                        {
                            Logger.Exception(String.Format("Database rollback failed! Please perform the rollback manually. Database backup file is stored at '{0}'.", backupFilePath), rbex.InnerException);
                        }
                }
                finally
                {
                    server.ConnectionContext.Disconnect();
                }
                throw;
            }
        }

        private void FreeUpgradeResources()
        {
            try
            {
                if (System.IO.File.Exists(backupFilePath))
                    System.IO.File.Delete(backupFilePath);
            }
            catch (Exception ex)
            {
                Logger.Exception(String.Format("Could not delete backup file '{0}'.", backupFilePath), ex);
            }
        }

        string backupFilePath;

        private void PrepareForUpgrade(Server server, String databaseName, String currentVersion)
        {
            if (UpgradeBackupAndRestoreOnError)
            {				
				backupFilePath = Path.Combine(BackupLocation ?? "", databaseName + String.Format("-{0}", currentVersion) + "-migration" + String.Format("-{0:yyyy-MM-dd hh-mm-ss-fff}", DateTime.Now) + ".bak");

                Logger.InfoFormat("Creating database backup at '{0}'.", backupFilePath);

                Backup bkpDBFull = new Backup();

                bkpDBFull.Action = BackupActionType.Database;
                bkpDBFull.Database = databaseName;

                bkpDBFull.Devices.AddDevice(backupFilePath, DeviceType.File);
                bkpDBFull.BackupSetName = String.Format("{0} database Backup. (Schema version: {1})", databaseName, currentVersion);
                bkpDBFull.BackupSetDescription = String.Format("{0} database - Full Backup. (Schema version: {1})", databaseName, currentVersion);
                bkpDBFull.ExpirationDate = DateTime.Today.AddDays(1);
                bkpDBFull.Initialize = true;
                bkpDBFull.PercentComplete += bkpDBFull_PercentComplete;
                bkpDBFull.Complete += bkpDBFull_Complete;
                bkpDBFull.SqlBackup(server);
            }
            else
                backupFilePath = null;
        }

        void bkpDBFull_Complete(object sender, ServerMessageEventArgs e)
        {
            Logger.Info("Database backup complete.");
        }

        void bkpDBFull_PercentComplete(object sender, PercentCompleteEventArgs e)
        {
            Logger.TraceFormat("Database backup {0}% complete.", e.Percent);
        }

        private void RollbackUpgrade(Server server, String databaseName)
        {
            if (backupFilePath != null)
            {
                Logger.InfoFormat("Restoring database from '{0}'.", backupFilePath);

                var db = server.Databases[databaseName];
                db.SetOffline();
                try
                {
                    Restore restoreDB = new Restore();
                    restoreDB.Database = databaseName;
                    /* Specify whether you want to restore database, files or log */
                    restoreDB.Action = RestoreActionType.Database;
                    restoreDB.Devices.AddDevice(backupFilePath, DeviceType.File);

                    restoreDB.ReplaceDatabase = true;
                    restoreDB.NoRecovery = false;

                    restoreDB.PercentComplete += restoreDB_PercentComplete;
                    restoreDB.Complete += restoreDB_Complete;

                    restoreDB.SqlRestore(server);
                }
                finally
                {
                    db.SetOnline();
                }
            }
        }

        void restoreDB_PercentComplete(object sender, PercentCompleteEventArgs e)
        {
            Logger.TraceFormat("Database restore {0}% complete.", e.Percent);
        }

        void restoreDB_Complete(object sender, ServerMessageEventArgs e)
        {
            Logger.Info("Database restored.");
        }

		String DefaultScriptVersionNumberGetter(String scriptName)
		{
			try
			{
				if (scriptName.Length > 4 && !Char.IsDigit(scriptName[4]))
				{
					var v = int.Parse(scriptName.Substring(0, 4));
					return v.ToString("0000");
				}
			}
			catch { }
			throw new InvalidOperationException();
		}
    }

}