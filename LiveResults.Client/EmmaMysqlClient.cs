﻿using System.Globalization;
using System.Web.UI.Design;
using MySql.Data.MySqlClient;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Linq;

namespace LiveResults.Client
{
    public delegate void LogMessageDelegate(string msg);

   
    public class EmmaMysqlClient : IDisposable
    {

        private static readonly Dictionary<int,Dictionary<string,int>> m_compsSourceToIdMapping = 
            new Dictionary<int, Dictionary<string, int>>(); 
        private static readonly Dictionary<int,int> m_compsNextGeneratedId = new Dictionary<int, int>();
        public string organizer;
        public DateTime compDate;
        public string compName;
        public static int GetIdForSourceIdInCompetition(int compId, string sourceId)
        {
            if (!m_compsSourceToIdMapping.ContainsKey(compId))
            {
                m_compsSourceToIdMapping.Add(compId, new Dictionary<string, int>());
                m_compsNextGeneratedId.Add(compId, -1);
            }
            if (!m_compsSourceToIdMapping[compId].ContainsKey(sourceId))
            {
                int id = m_compsNextGeneratedId[compId]--;
                m_compsSourceToIdMapping[compId][sourceId] = id;
                return id;
            }
            else
            {
                return m_compsSourceToIdMapping[compId][sourceId];
            }
        }

        public struct EmmaServer
        {
            public string Host;
            public string User;
            public string Pw;
            public string DB;
        }
        public static EmmaServer[] GetServersFromConfig()
        {
            var servers = new List<EmmaServer>();
            int sNum = 1;
            while (true)
            {
                string server = ConfigurationManager.AppSettings["emmaServer" + sNum];
                if (server == null)
                    break;

                string[] parts = server.Split(';');
                var s = new EmmaServer();
                s.Host = parts[0];
                s.User = parts[1];
                s.Pw = parts[2];
                s.DB = parts[3];

                servers.Add(s);
                sNum++;

            }
            if (!string.IsNullOrEmpty(ConfigurationManager.AppSettings["serverpollurl"]))
            {
                try
                {
                    WebRequest wq = WebRequest.Create(ConfigurationManager.AppSettings["serverpollurl"]);
                    wq.Method = "POST";
                    byte[] data = Encoding.ASCII.GetBytes("key=" + ConfigurationManager.AppSettings["serverpollkey"]);
                    wq.ContentLength = data.Length;
                    wq.ContentType = "application/x-www-form-urlencoded";
                    Stream st = wq.GetRequestStream();
                    st.Write(data, 0, data.Length);
                    st.Flush();
                    st.Close();
                    WebResponse ws = wq.GetResponse();
                    Stream responseStream = ws.GetResponseStream();
                    if (responseStream != null)
                    {
                        var sr = new StreamReader(responseStream);
                        string resp = sr.ReadToEnd();
                        if (resp.Trim().Length > 0)
                        {
                            string[] lines = resp.Trim().Split('\n');
                            foreach (string line in lines)
                            {
                                string[] parts = line.Split(';');
                                var s = new EmmaServer();
                                s.Host = parts[0];
                                s.User = parts[1];
                                s.Pw = parts[2];
                                s.DB = parts[3];

                                servers.Add(s);
                            }
                        }
                    }
                }
                catch (Exception ee)
                {
                    System.Windows.Forms.MessageBox.Show("Could not connect to " + new Uri(ConfigurationManager.AppSettings["serverpollurl"]).Host + " to query connection, error was: " + ee.Message + "\r\n\r\nStacktrace: " + ee.StackTrace);
                }
            }

            return servers.ToArray();
        }

        public event LogMessageDelegate OnLogMessage;
        private MySqlConnection m_connection;
        private readonly string m_connStr;
        private readonly int m_compID;
        private readonly Dictionary<int,Runner> m_runners;
        private readonly Dictionary<string, RadioControl[]> m_classRadioControls;
        private readonly List<DbItem> m_itemsToUpdate;
        private readonly bool m_assignIDsInternally;
        private int m_nextInternalId = 1;

        public EmmaMysqlClient(string server, int port, string user, string pass, string database, int competitionID, bool assignIDsInternally = false)
        {
            m_runners = new Dictionary<int, Runner>();
            m_classRadioControls = new Dictionary<string, RadioControl[]>();
            m_itemsToUpdate = new List<DbItem>();
            m_assignIDsInternally = assignIDsInternally;

            m_connStr = "Database=" + database + ";Data Source="+server+";User Id="+user+";Password="+pass;
            m_connection = new MySqlConnection(m_connStr);
            m_compID = competitionID;
        }

        private void ResetUpdated()
        {
            foreach (Runner r in m_runners.Values)
            {
                r.RunnerUpdated = false;
                r.ResultUpdated = false;
                r.ResetUpdatedSplits();
            }
        }

       
        private void FireLogMsg(string msg)
        {
            if (OnLogMessage != null)
                OnLogMessage(msg);
        }

        public Runner GetRunner(int dbId)
        {
            if (!IsRunnerAdded(dbId))
                return null;
            return m_runners[dbId];
        }

        private bool m_continue;
        private bool m_currentlyBuffering;
        private Thread m_mainTh;

 

        public void Start()
        {
            FireLogMsg("Buffering existing results..");
            int numRunners = 0;
            int numResults = 0;
            try
            {
                m_currentlyBuffering = true;
                m_connection.Open();

                SetCodePage(m_connection);

                MySqlCommand cmd = m_connection.CreateCommand();

                if (!m_compsSourceToIdMapping.ContainsKey(m_compID))
                {
                    m_compsSourceToIdMapping.Add(m_compID, new Dictionary<string, int>());
                    m_compsNextGeneratedId.Add(m_compID, -1);
                }

                cmd.CommandText = "select classname,corder,code,name from splitcontrols where tavid = " + m_compID;
                MySqlDataReader reader = cmd.ExecuteReader();
                Dictionary<string, List<RadioControl>> tmpRadios = new Dictionary<string, List<RadioControl>>();
                while (reader.Read())
                {
                    string className = reader["classname"] as string;
                    int corder = Convert.ToInt32(reader["corder"]);
                    int code = Convert.ToInt32(reader["code"]);
                    string name = reader["name"] as string;

                    if (!tmpRadios.ContainsKey(className))
                        tmpRadios.Add(className, new List<RadioControl>());

                    tmpRadios[className].Add(new RadioControl() { ClassName = className, Code = code, ControlName = name, Order = corder });
                }
                reader.Close();
                foreach (var kvp in tmpRadios)
                {
                    m_classRadioControls.Add(kvp.Key, kvp.Value.ToArray());
                }

                cmd.CommandText = "select sourceid,id from runneraliases where compid = " + m_compID;
                reader = cmd.ExecuteReader();

                Dictionary<int,string> idToAliasDictionary = new Dictionary<int, string>();

                while (reader.Read())
                {
                    var sourceId = reader["sourceid"] as string;
                    if (sourceId == null)
                        continue;
                    int id = Convert.ToInt32(reader["id"]);
                    if (!m_compsSourceToIdMapping[m_compID].ContainsKey(sourceId))
                    {
                        m_compsSourceToIdMapping[m_compID].Add(sourceId, id);
                        if (id <= m_compsNextGeneratedId[m_compID])
                            m_compsNextGeneratedId[m_compID] = id - 1;
                    }
                }
                reader.Close();

                foreach (var kvp in m_compsSourceToIdMapping[m_compID])
                {
                    if (!idToAliasDictionary.ContainsKey(kvp.Value))
                        idToAliasDictionary.Add(kvp.Value, kvp.Key);
                }

                cmd.CommandText = "select organizer, compDate, compName from login where tavid = " + m_compID;
                reader = cmd.ExecuteReader();
                reader.Read();

                organizer    = reader[("organizer")] as string;
                compName     = reader[("compName")] as string;
                compDate     = Convert.ToDateTime(reader[("compDate")]);
                

                reader.Close();

                cmd.CommandText = "select runners.dbid,control,time,name,club,class,status from runners, results where results.dbid = runners.dbid and results.tavid = " + m_compID + " and runners.tavid = " + m_compID;
                reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var dbid     = Convert.ToInt32(reader["dbid"]);
                    var control  = Convert.ToInt32(reader["control"]);
                    var time     = Convert.ToInt32(reader["time"]);
                    var sourceId = idToAliasDictionary.ContainsKey(dbid) ? idToAliasDictionary[dbid] : null;
                    if (!IsRunnerAdded(dbid))
                    {
                        var r = new Runner(dbid, reader["name"] as string, reader["club"] as string, reader["class"] as string, sourceId as string);
                        AddRunner(r);
                        numRunners++;
                    }
                switch (control)
                    {
                        case 1000:
                            SetRunnerResult(dbid, time, Convert.ToInt32(reader["status"]));
                            numResults++;
                            break;
                        case 100:
                            SetRunnerStartTime(dbid, time);
                            numResults++;
                            break;
                        default:
                            numResults++;
                            SetRunnerSplit(dbid, control, time);
                            break;
                    }
                    
                }
                reader.Close();
                cmd.Dispose();

                ResetUpdated();
            }
            catch (Exception ee)
            {
                FireLogMsg(ee.Message);
                Thread.Sleep(1000);
            }
            finally
            {
                m_connection.Close();
                m_itemsToUpdate.Clear();
                m_currentlyBuffering = false;
                FireLogMsg("Done - Buffered " + m_runners.Count + " existing runners and " + numResults +" existing results from server");
            }
            
            m_continue = true;
            m_mainTh = new Thread(Run);
            m_mainTh.Name = "Main MYSQL Thread [" + m_connection.DataSource + "]";
            m_mainTh.Start();

            if (m_assignIDsInternally)
            {
                m_nextInternalId = m_runners.Count > 0 ? m_runners.Keys.Max() + 1 : 1;
            }
        }

        public bool UpdateRunnerInfo(int id, string name, string club, string Class, string sourceId)
        {
            if (m_runners.ContainsKey(id))
            {
                var cur = m_runners[id];
                if (cur == null)
                    return false;
                bool isUpdated = false;
                if (cur.Name != name)
                {
                    cur.Name = name;
                    isUpdated = true;
                }
                if (cur.Class != Class)
                {
                    cur.Class = Class;
                    isUpdated = true;
                }
                if (cur.Club != club)
                {
                    cur.Club = club;
                    isUpdated = true;
                }
                if (string.IsNullOrEmpty(sourceId))
                    sourceId = null;
                if (string.IsNullOrEmpty(cur.SourceId))
                    cur.SourceId = null;
                if (cur.SourceId != sourceId && sourceId != id.ToString(CultureInfo.InvariantCulture))
                {
                    cur.SourceId = sourceId;
                    isUpdated = true;
                }
                if (isUpdated)
                {
                    cur.RunnerUpdated = true;
                    m_itemsToUpdate.Add(cur);

                    if (!m_currentlyBuffering)
                    {
                        FireLogMsg("Runnerinfo changed [" + cur.Name + "]");
                    }
                }
                return true;
            }
            else return false; // Not existing
        }

        /// <summary>
        /// Adds a Runner to this competition
        /// </summary>
        /// <param name="r"></param>
        public void AddRunner(Runner r)
        {
            if (!m_runners.ContainsKey(r.ID))
            {
                m_runners.Add(r.ID, r);
                m_itemsToUpdate.Add(r);
                if (!m_currentlyBuffering)
                {
                    FireLogMsg("Runner added [" + r.Name + "]");
                }
            }
        }

        /// <summary>
        /// Delete a runner from this competition
        /// </summary>
        /// <param name="r"></param>
        public void RemoveRunner(Runner r)
        {
            if (m_runners.ContainsKey(r.ID))
            {
                m_runners.Remove(r.ID);
                m_itemsToUpdate.Add(new DelRunner { RunnerID = r.ID });
                if (!m_currentlyBuffering)
                {
                    FireLogMsg("Runner deleted [" + r.Name + ", " + r.Class + "]");
                }
            }
        }

        public void SetRadioControl(string className, int code, string controlName, int order)
        {
            m_itemsToUpdate.Add(new RadioControl
            {
                ClassName = className,
                Code = code,
                ControlName = controlName,
                Order = order
            });
        }

        public int UpdatesPending
        {
            get
            {
                return m_itemsToUpdate.Count;
            }
        }

        /// <summary>
        /// Returns true if a runner with the specified runnerid exist in the competition
        /// </summary>
        /// <param name="runnerID"></param>
        /// <returns></returns>
        public bool IsRunnerAdded(int runnerID)
        {
            return m_runners.ContainsKey(runnerID);
        }

        /// <summary>
        /// Sets the result for the runner with runnerID
        /// </summary>
        /// <param name="runnerID"></param>
        /// <param name="time"></param>
        /// <param name="status"></param>
        public void SetRunnerResult(int runnerID, int time, int status)
        {
            if (!IsRunnerAdded(runnerID))
                throw new ApplicationException("Runner is not added! {" + runnerID + "} [SetRunnerResult]");

            var r = m_runners[runnerID];

            if (r.HasResultChanged(time, status))
            {
                r.SetResult(time, status);
                m_itemsToUpdate.Add(r);
                if (!m_currentlyBuffering)
                {
                    FireLogMsg("Runner result changed: [" + r.Name + ", " + r.Time + "]");
                }
            }
        }

        public void SetRunnerSplit(int runnerID, int controlcode, int time)
        {
            if (!IsRunnerAdded(runnerID))
                throw new ApplicationException("Runner is not added! {" + runnerID + "} [SetRunnerResult]");
            var r = m_runners[runnerID];

            if (r.HasSplitChanged(controlcode, time))
            {
                r.SetSplitTime(controlcode, time);
                m_itemsToUpdate.Add(r);
                if (!m_currentlyBuffering)
                {
                    FireLogMsg("Runner Split Changes: [" + r.Name + ", {cn: " + controlcode + ", t: " + time + "}]");
                }
            }

        }

        public void SetRunnerStartTime(int runnerID, int starttime)
        {
            if (!IsRunnerAdded(runnerID))
                throw new ApplicationException("Runner is not added! {" + runnerID + "} [SetRunnerStartTime]");
            var r = m_runners[runnerID];

            if (r.HasStartTimeChanged(starttime))
            {
                r.SetStartTime(starttime);
                m_itemsToUpdate.Add(r);
                if (!m_currentlyBuffering)
                {
                    FireLogMsg("Runner starttime Changed: [" + r.Name + ", t: " + starttime + "}]");
                }
            }

        }

        public void MergeRadioControls(RadioControl[] radios)
        {
            if (radios == null)
                return;

            foreach (var kvp in radios.GroupBy(x => x.ClassName))
            {
                RadioControl[] controls = kvp.OrderBy(x => x.Order).ToArray();
                if (m_classRadioControls.ContainsKey(kvp.Key))
                {
                    RadioControl[] existingRadios = m_classRadioControls[kvp.Key];
                    for (int i = 0; i < controls.Length; i++)
                    {
                        if (existingRadios.Length > i)
                        {
                            if (existingRadios[i].Order != controls[i].Order 
                                || existingRadios[i].Code != controls[i].Code
                                || existingRadios[i].ControlName != controls[i].ControlName)
                            {
                                m_itemsToUpdate.Add(new DelRadioControl() { ToDelete = existingRadios[i] });
                                m_itemsToUpdate.Add(controls[i]);
                            }
                        }
                        else
                        {
                            m_itemsToUpdate.Add(controls[i]);
                        }
                    }
                    if (existingRadios.Length > controls.Length)
                    {
                        for (int i = controls.Length; i < existingRadios.Length; i++)
                        {
                            m_itemsToUpdate.Add(new DelRadioControl() { ToDelete = existingRadios[i] });
                        }
                    }
                    m_classRadioControls[kvp.Key] = controls;

                }
                else
                {
                    foreach (var control in controls)
                    {
                        m_itemsToUpdate.Add(control);
                    }
                    m_classRadioControls.Add(kvp.Key, controls);
                }
            }
        }

        public void MergeRunners(Runner[] runners)
        {
            if (runners == null)
                return;

            foreach (var r in runners)
            {
                if (!IsRunnerAdded(r.ID))
                {
                    AddRunner(new Runner(r.ID, r.Name, r.Club, r.Class, r.SourceId));
                }
                else
                {
                    UpdateRunnerInfo(r.ID, r.Name, r.Club, r.Class, r.SourceId);
                }


                UpdateRunnerTimes(r);
            }
        }

        public void UpdateCurrentResultsFromNewSet(Runner[] runners)
        {
            if (runners == null)
                return;

            // Update or add runner
            foreach (var runner in runners)
            {
                //if (m_runners.ContainsKey(runner.ID))
                //{
                bool updated = UpdateRunnerInfo(runner.ID, runner.Name, runner.Club, runner.Class, runner.SourceId);
                //}
                //else
                if (!updated)
                {
                    var newRunner = new Runner(runner.ID, runner.Name, runner.Club, runner.Class, runner.SourceId);
                    AddRunner(newRunner);
                }
                UpdateRunnerTimes(runner);
            }


            // Check if existing runners has been removed in XML file
            if (m_runners.Count > runners.Length) // More runners in DB than in XML file
            {
                var existingRunners = m_runners.Values;
                foreach (var existingRunner in existingRunners.ToList())
                {
                    bool exists = false;
                    foreach (var runner in runners)
                    {
                        if (existingRunner.ID == runner.ID)
                        {
                            exists = true;
                            break;
                        }
                    }
                    if (!exists)
                    {
                        RemoveRunner(existingRunner);
                    }
                }
            }
        }

        public void DeleteUnusedRunners(List<int> usedIds)
        {
            if (usedIds == null)
                return;
           
            var dbRunners = m_runners.Values;
                foreach (var dbRunner in dbRunners.ToList())
                {
                    if (!usedIds.Contains(dbRunner.ID))
                        RemoveRunner(dbRunner);
                }
        }

        private void UpdateRunnerTimes(Runner runner)
        {
            if (runner.StartTime >= 0)
                SetRunnerStartTime(runner.ID, runner.StartTime);

            SetRunnerResult(runner.ID, runner.Time, runner.Status);

            var spl = runner.SplitTimes;
            if (spl != null)
            {
                foreach (var s in spl)
                {
                    SetRunnerSplit(runner.ID, s.Control, s.Time);
                }
            }
        }


        public void Stop()
        {
            m_continue = false;
        }

        private void Run()
        {
            while (m_continue)
            {
                try
                {
                    m_connection = new MySqlConnection(m_connStr);
                    m_connection.Open();
                    SetCodePage(m_connection);
                    while (m_continue)
                    {
                        if (m_itemsToUpdate.Count > 0)
                        {
                            using (MySqlCommand cmd = m_connection.CreateCommand())
                            {
                                var item = m_itemsToUpdate[0];
                                if (item is RadioControl)
                                {
                                    var r = item as RadioControl;
                                    cmd.Parameters.Clear();
                                    cmd.Parameters.AddWithValue("?compid", m_compID);
                                    cmd.Parameters.AddWithValue("?name", Encoding.UTF8.GetBytes(r.ClassName));
                                    cmd.Parameters.AddWithValue("?corder", r.Order);
                                    cmd.Parameters.AddWithValue("?code", r.Code);
                                    cmd.Parameters.AddWithValue("?cname", Encoding.UTF8.GetBytes(r.ControlName));
                                    cmd.CommandText = "REPLACE INTO splitcontrols(tavid,classname,corder,code,name) VALUES (?compid,?name,?corder,?code,?cname)";

                                    try
                                    {
                                        cmd.ExecuteNonQuery();
                                    }
                                    catch (Exception ee)
                                    {
                                        //Move failing runner last
                                        m_itemsToUpdate.Add(r);
                                        m_itemsToUpdate.RemoveAt(0);
                                        throw new ApplicationException("Could not add radiocontrol " + r.ControlName + ", " + r.ClassName + ", " + r.Code + " to server due to: " + ee.Message, ee);
                                    }
                                    cmd.Parameters.Clear();
                                }
                                else if (item is DelRadioControl)
                                {
                                    var dr = item as DelRadioControl;
                                    var r = dr.ToDelete;
                                    cmd.Parameters.Clear();
                                    cmd.Parameters.AddWithValue("?compid", m_compID);
                                    cmd.Parameters.AddWithValue("?name", Encoding.UTF8.GetBytes(r.ClassName));
                                    cmd.Parameters.AddWithValue("?corder", r.Order);
                                    cmd.Parameters.AddWithValue("?code", r.Code);
                                    cmd.Parameters.AddWithValue("?cname", Encoding.UTF8.GetBytes(r.ControlName));
                                    cmd.CommandText = "delete from splitcontrols where tavid= ?compid and classname = ?name and corder = ?corder and code = ?code and name = ?cname";

                                    try
                                    {
                                        cmd.ExecuteNonQuery();
                                    }
                                    catch (Exception ee)
                                    {
                                        //Move failing runner last
                                        m_itemsToUpdate.Add(r);
                                        m_itemsToUpdate.RemoveAt(0);
                                        throw new ApplicationException("Could not delete radiocontrol " + r.ControlName + ", " + r.ClassName + ", " + r.Code + " to server due to: " + ee.Message, ee);
                                    }
                                    cmd.Parameters.Clear();
                                }
                                else if (item is DelRunner)
                                {
                                    var dr = item as DelRunner;
                                    var r = dr.RunnerID;
                                    cmd.Parameters.Clear();
                                    cmd.Parameters.AddWithValue("?compid", m_compID);
                                    cmd.Parameters.AddWithValue("?id", r);
                                    cmd.CommandText = "delete from results where tavid= ?compid and dbid = ?id";
                                    try
                                    {
                                        cmd.ExecuteNonQuery();
                                        cmd.CommandText = "delete from runners where tavid= ?compid and dbid = ?id";
                                        cmd.ExecuteNonQuery();
                                        cmd.CommandText = "delete from runneraliases where compid= ?compid and id = ?id";
                                        cmd.ExecuteNonQuery();
                                    }
                                    catch (Exception ee)
                                    {
                                        //Move failing runner last
                                        m_itemsToUpdate.Add(dr);
                                        m_itemsToUpdate.RemoveAt(0);
                                        throw new ApplicationException("Could not delete runner " + r + " on server due to: " + ee.Message, ee);
                                    }
                                    cmd.Parameters.Clear();
                                }
                                else if (item is Runner)
                                {
                                    var r = item as Runner;
                                    if (r.RunnerUpdated)
                                    {
                                        cmd.Parameters.Clear();
                                        cmd.Parameters.AddWithValue("?compid", m_compID);
                                        cmd.Parameters.AddWithValue("?name", Encoding.UTF8.GetBytes(r.Name));
                                        cmd.Parameters.AddWithValue("?club", Encoding.UTF8.GetBytes(r.Club ?? ""));
                                        cmd.Parameters.AddWithValue("?class", Encoding.UTF8.GetBytes(r.Class));

                                        cmd.Parameters.AddWithValue("?id", r.ID);
                                        cmd.CommandText = "REPLACE INTO runners (tavid,name,club,class,brick,dbid) VALUES (?compid,?name,?club,?class,0,?id)";
                                       

                                        try
                                        {
                                            cmd.ExecuteNonQuery();
                                        }
                                        catch (Exception ee)
                                        {
                                            // Move failing runner last
                                            m_itemsToUpdate.Add(r);
                                            m_itemsToUpdate.RemoveAt(0);
                                            throw new ApplicationException(
                                                "Could not add runner " + r.Name + ", " + r.Club + ", " + r.Class + " to server due to: " + ee.Message, ee);
                                        }
                                        cmd.Parameters.Clear();

                                        if (!string.IsNullOrEmpty(r.SourceId) && r.SourceId != r.ID.ToString(CultureInfo.InvariantCulture))
                                        {
                                            cmd.CommandText = "REPLACE INTO runneraliases (compid,sourceid,id) VALUES (?compid,?sourceId,?id)";
                                            cmd.Parameters.AddWithValue("?compid", m_compID);
                                            cmd.Parameters.AddWithValue("?sourceId", r.SourceId);
                                            cmd.Parameters.AddWithValue("?id", r.ID);
                                            try
                                            {
                                                cmd.ExecuteNonQuery();
                                            }
                                            catch (Exception ee)
                                            {
                                                FireLogMsg("Could not add alias for runner " + r.Name + " in DB [" + ee.Message + "]");
                                            }
                                        }

                                        FireLogMsg("Runner " + r.Name + " updated in DB");
                                        r.RunnerUpdated = false;
                                    }
                                    if (r.ResultUpdated)
                                    {
                                        cmd.Parameters.Clear();
                                        cmd.Parameters.AddWithValue("?compid", m_compID);
                                        cmd.Parameters.AddWithValue("?id", r.ID);
                                        cmd.Parameters.AddWithValue("?time", r.Time);
                                        cmd.Parameters.AddWithValue("?status", r.Status);
                                        cmd.CommandText = "REPLACE INTO results (tavid,dbid,control,time,status,changed) VALUES(?compid,?id,1000,?time,?status,Now())";
                                        cmd.ExecuteNonQuery();
                                        cmd.Parameters.Clear();

                                        FireLogMsg("Runner " + r.Name + "s result updated in DB");
                                        r.ResultUpdated = false;
                                    }
                                    if (r.StartTimeUpdated)
                                    {
                                        cmd.Parameters.Clear();
                                        cmd.Parameters.AddWithValue("?compid", m_compID);
                                        cmd.Parameters.AddWithValue("?id", r.ID);
                                        cmd.Parameters.AddWithValue("?starttime", r.StartTime);
                                        cmd.Parameters.AddWithValue("?status", r.Status);
                                        cmd.CommandText = "REPLACE INTO results (tavid,dbid,control,time,status,changed) VALUES(?compid,?id,100,?starttime,?status,Now())";
                                        cmd.ExecuteNonQuery();
                                        cmd.Parameters.Clear();
                                        FireLogMsg("Runner " + r.Name + "s starttime updated in DB");
                                        r.StartTimeUpdated = false;
                                    }
                                    if (r.HasUpdatedSplitTimes())
                                    {
                                        List<SplitTime> splitTimes = r.GetUpdatedSplitTimes();

                                        cmd.Parameters.Clear();
                                        cmd.Parameters.AddWithValue("?compid", m_compID);
                                        cmd.Parameters.AddWithValue("?id", r.ID);
                                        cmd.Parameters.AddWithValue("?control", -1);
                                        cmd.Parameters.AddWithValue("?time", -1);
                                        foreach (SplitTime t in splitTimes)
                                        {
                                            cmd.Parameters["?control"].Value = t.Control;
                                            cmd.Parameters["?time"].Value = t.Time;
                                            cmd.CommandText = "REPLACE INTO results (tavid,dbid,control,time,status,changed) VALUES(" + m_compID + "," + r.ID + "," + t.Control + "," + t.Time +
                                                              ",0,Now())";
                                            cmd.ExecuteNonQuery();
                                            t.Updated = false;
                                            FireLogMsg("Runner " + r.Name + " splittime{" + t.Control + "} updated in DB");
                                        }
                                        cmd.Parameters.Clear();
                                    }
                                }

                                m_itemsToUpdate.RemoveAt(0);
                            }
                        }
                        else
                        {
                            Thread.Sleep(100);
                        }
                    }

                }
                catch (Exception ee)
                {
                    FireLogMsg("Error: " + ee.Message + (m_connection != null ? " [" + m_connection.DataSource + "]" : ""));
                    System.Diagnostics.Debug.Write(ee.Message);
                    Thread.Sleep(1000);
                }
                finally
                {
                    if (m_connection != null)
                    {
                        m_connection.Close();
                        m_connection.Dispose();
                        m_connection = null;
                    }
                }
            }
        }

        private void SetCodePage(MySqlConnection conn)
        {
            using (MySqlCommand cmd = conn.CreateCommand())
            {
                cmd.CommandText = "set names 'utf8'";
                if (!string.IsNullOrEmpty(ConfigurationManager.AppSettings["server_charset"]))
                {
                    cmd.CommandText = "set names '" + ConfigurationManager.AppSettings["server_charset"] + "'";
                }
                cmd.ExecuteNonQuery();
            }
        }

        public override string ToString()
        {
            return (m_connection != null ? m_connection.DataSource : "Detached") + " (" + UpdatesPending + ")";
        }

        #region IDisposable Members

        void IDisposable.Dispose()
        {
            if (m_connection != null)
            {
                m_connection.Close();
                m_connection.Dispose();
            }
        }

        #endregion
    }
}
