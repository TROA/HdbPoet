using System;
using System.Data;
using System.Linq;
using System.IO;
using System.Diagnostics;
using Reclamation.Core;
using System.Configuration;
using System.Collections.Generic;
using System.Text.RegularExpressions;
namespace HdbPoet
{
    /// <summary>
    /// HDB time series database
    /// </summary>
    public partial class Hdb
    {
        OracleServer m_server;
        DataTable _object_types;


        public Hdb(OracleServer server)
        {
            m_server = server;
            LookupApplication();
        }

        public static Hdb Instance;

        private static Dictionary<string, Hdb> s_dict = new Dictionary<string, Hdb>();


        /// <summary>
        /// Gets a instance to a hdb,  used by Pisces to support multiple databases
        /// </summary>
        /// <param name="hostname"></param>
        /// <returns></returns>
        public static Hdb GetInstance(string hostname)
        {
            if (Instance != null &&  Instance.Server.Host == hostname)
            {
                return Instance;
            }
            else if( s_dict.ContainsKey(hostname))
            {
                return s_dict[hostname];
            }
            else
            {
               // To  do..  set hostname for Login user-interface
                var svr = OracleServer.ConnectToOracle(hostname);
                if (svr == null)
                    return null;

                Hdb hdb1 = new Hdb(svr);
                s_dict.Add(hostname, hdb1);
                return hdb1;
            }
        }

        public OracleServer Server
        {
            get { return m_server; }
            set { m_server = value; }
        }
        
        static string[] r_tables = { "r_instant", "r_hour", "r_day", "r_month", "r_year", "r_wy","r_base" };
        public static string[] r_names = { "instant", "hour", "day", "month", "year", "wy" ,"base"};

        static bool? m_isAclAdmin;
        public bool IsAclAdministrator
        {
            get
            {
                if (m_isAclAdmin.HasValue)
                    return m_isAclAdmin.Value;

                m_isAclAdmin = (m_server.Table("a", "select * from ref_user_groups where user_name = '" +  m_server.Username.ToUpper() + "' and group_name= 'DBA'").Rows.Count > 0);

                return m_isAclAdmin.Value;
            }
        }


        /// <summary>
        /// Convert from Local Time zone to HDB time zone
        /// Used to convert Dates entered from the user interface.
        /// </summary>
        /// <param name="date"></param>
        /// <returns></returns>
        private string ToHdbTimeZone(DateTime date, string interval,string timeZone)
        {
            string rval = "";
            bool adjustTimeZone = IsTimeZoneAdjustmentNeeded(interval);

            if (adjustTimeZone && timeZone != m_server.TimeZone)
                rval = " new_time ( "
            //+ "TO_Date('" + date.ToString("MM/dd/yyyy HH:mm:ss") + "', 'MM/dd/yyyy HH24:MI:SS') "
            + ToHdbDate(date)
            + ", '" + timeZone + "', '" + m_server.TimeZone + "')";

            else
                rval = ToHdbDate(date); //" TO_Date('" + date.ToString("MM/dd/yyyy HH:mm:ss") + "', 'MM/dd/yyyy HH24:MI:SS') ";

            return rval;
        }
        public static string ToHdbDate(DateTime date)
        {
            return "TO_Date('" + date.ToString("MM/dd/yyyy HH:mm:ss") + "', 'MM/dd/yyyy HH24:MI:SS') ";
        }


        private static bool IsTimeZoneAdjustmentNeeded(string interval)
        {
            if (interval == "hour" || interval == "instant")
                return true;
            return false;
        }

        private string ToLocalTimeZone(string dateColumnName, string interval, string timeZone)
        {
            bool adjustTimeZone = IsTimeZoneAdjustmentNeeded(interval);
            
            if (adjustTimeZone && timeZone != Server.TimeZone)
                return "new_time(" + dateColumnName + ","
         + "'" + m_server.TimeZone + "', '" + timeZone + "') as date_time ";

               return dateColumnName;
        }

        public string[] ValidationList()
        {
            string sql = "select validation ||'  ' ||cmmnt as item from hdb_validation " // where validation in ('A','F','L','H','P','T','V') "
                + "UNION  SELECT '    <<null>>' from dual ";
          DataTable tbl = m_server.Table("validation", sql);
          return DataTableUtility.StringList(tbl, "", "item").ToArray();
        }


        /// <summary>
        /// Delete or Update each modified value in DataSet
        /// </summary>
        /// <param name="interval"></param>
        /// <param name="ds"></param>
        /// <param name="overWrite"></param>
        /// <param name="validationFlag"></param>
        public void SaveChanges(string interval, 
            GraphData ds,bool overWrite, char validationFlag )
        {
            //for (int i = 0; i < ds.Series.Count; i++)
            foreach (var s in ds.SeriesRows)
            {
                if (String.Compare(s.Interval, interval, true) == 0)
                {
                    DataTable tbl = ds.GetTable(s.TableName);
                    DataTableUtility.PrintRowState(tbl);
                    DataRow[] rows = tbl.Select("", "", DataViewRowState.ModifiedCurrent);

                    foreach (DataRow  row in rows)
                    {
                        DateTime t = Convert.ToDateTime(row[0]);
                        if ( row[1, DataRowVersion.Current] == DBNull.Value
                            && row[1, DataRowVersion.Original] != DBNull.Value )
                        {// Delete
                           delete_from_hdb(s.hdb_site_datatype_id, t,
                                interval,ds.GraphRow.TimeZone);
                        }
                        else
                        {// update
                            double val =Convert.ToDouble(row[1]);
                            ModifyRase(s.hdb_site_datatype_id, interval, t, val, overWrite,
                                validationFlag,ds.GraphRow.TimeZone);
                        }
                    }
                    tbl.AcceptChanges();
                }
            }
        }


        /// <summary>
        /// Check if this is a computed series.
        /// </summary>
        private bool IsComputed(int sdi) 
        {
            //string sql = "Select algo_role_name from cp_comp_ts_parm where Site_DataType_ID = " + sdi
              //  + " and algo_role_name = 'output'";

            string sql = "select distinct ccts.site_datatype_id,ccts.interval "
	 + "from  cp_computation cc, cp_comp_ts_parm ccts, cp_algo_ts_parm catp "
	+ " where "
	+" cc.enabled = 'Y' "
	+" and  cc.loading_application_id is not null "
    +"and  cc.computation_id = ccts.computation_id "
	+" and  cc.algorithm_id = catp.algorithm_id "
	+" and  ccts.algo_role_name = catp.algo_role_name "
	+" and  catp.parm_type like 'o%' "
    +"	and  ccts.site_datatype_id = " + sdi;


            var tbl = Server.Table("iscomputed", sql);

            return tbl.Rows.Count > 0;
        }



        /// <summary>
        /// Returns true if site coressponding to sdi is readonly.
        /// any site not in the config file is read only
        /// </summary>
        /// <param name="sdi"></param>
        /// <returns></returns>
        public bool ReadOnly(int sdi)
        {
            return Hdb.Instance.AclReadonly(sdi);
        }

        /// <summary>
        /// Reads time series data from HDB
        /// </summary>
        /// <param name="site_datatype_id"></param>
        /// <param name="r_table">one of: r_instant, r_hour, r_day, r_month, r_year, r_wy, r_base</param>
        /// <param name="interval">one of: day,hour,month,year,wy,instant</param>
        /// <param name="instantInterval">if interval is 'instant' this is minutes between points</param>
        /// <param name="t1">Beginning DateTime</param>
        /// <param name="t2">Ending DateTime</param>
        /// <returns></returns>
        public DataTable Table(decimal site_datatype_id, string r_table,
            string interval, int instantInterval,
          DateTime t1, DateTime t2,string timeZone)
        {

            if (timeZone == "")
                timeZone = "MST";

            t1 = AdjustDateToMatchInterval(interval, t1);

            string dateSubquery = " table(dates_between( " + ToHdbTimeZone(t1, interval, timeZone) + ", "
                                      + ToHdbTimeZone(t2, interval, timeZone) + ",'" +interval + "')) A ";
            if (interval == "instant")
            {

                dateSubquery = " table(instants_between( " + ToHdbTimeZone(t1, interval, timeZone) + ", "
                                             + ToHdbTimeZone(t2, interval, timeZone) + "," + instantInterval + ")) A ";
            }

            string wherePreamble = " where ";
            if (r_table == "r_base")
                wherePreamble += " interval(+) = '" + interval + "' and ";


            string sql = 
             "select " + ToLocalTimeZone("A.date_time", interval, timeZone) + ", B.value, "
           + "colorize_with_rbase(" + site_datatype_id.ToString() + ", '" + interval + "', B.start_date_time,B.value ) as SourceColor, "
           + "colorize_with_validation(" + site_datatype_id.ToString() + ", '" + interval + "', A.date_time, B.value ) as ValidationColor "
           + " from " + r_table + " B , "
           + dateSubquery 
           + wherePreamble 
           + " B.start_date_time(+) = A.date_time and site_datatype_id(+) = " + site_datatype_id 
           + " and B.start_date_time(+) >= " + ToHdbTimeZone(t1, interval, timeZone) + "\n "
           + " and B.start_date_time(+) <= " + ToHdbTimeZone(t2, interval, timeZone) + "\n "
           + " order by A.date_time";
            
            DataTable rval = m_server.Table(r_table, sql);
            if (rval == null)
            {
                rval = new DataTable();
                rval.Columns.Add(new DataColumn("date_time", typeof(DateTime)));
                rval.Columns.Add(new DataColumn("value", typeof(double)));
            }

            rval.Columns["date_time"].Unique = true;
            rval.PrimaryKey = new DataColumn[] { rval.Columns["date_time"] };
            rval.DefaultView.Sort = rval.Columns[0].ColumnName;
            rval.DefaultView.ApplyDefaultSort = true;
            rval.Columns[0].DefaultValue = DBNull.Value;
            rval.Columns["SourceColor"].DefaultValue = "LightGray";
            rval.Columns["ValidationColor"].DefaultValue = "LightGray";

            Logger.WriteLine("read " + rval.Rows.Count + " rows ");
            return rval;
        }

        public static DateTime AdjustDateToMatchInterval(string interval, DateTime t1)
        {
            switch (interval)
            {
                case "hour":// rhour hh:00
                    t1 = new DateTime(t1.Year, t1.Month, t1.Day, t1.Hour, 0, 0);
                    break;
                case "day": // rday 00:00
                    t1 = t1.Date;
                    break;
                case "month": // rmonth beginning of month
                    t1 = new DateTime(t1.Year, t1.Month, 1);
                    break;
                case "year": // ryear  beginning of year 
                    t1 = new DateTime(t1.Year, 1, 1);
                    break;
                case "wy": // r_wy   beginning of water year.
                    if( t1.Month >= 10)
                       t1 = new DateTime(t1.Year, 10, 1);
                    else
                       t1 = new DateTime(t1.Year-1, 10, 1);
                    break;
                case "instant": // instant (round to 15 minute)
                    t1 = new DateTime(t1.Year,t1.Month,t1.Day,t1.Hour,0,0);
                    break;
                default:
                    break;
            }
            return t1;
        }

        /// <summary>
        /// Read time series data from Oracle 
        /// as specified in the TimeSeriesDataSet
        /// </summary>
        /// <param name="ds"></param>
        public void Fill(GraphData gd)
        {
            
            if (gd.SeriesRows.Count() == 0)
            {
                return;
            }

            int idx = 0;

            foreach (var s in gd.SeriesRows)
            {
                s.IsComputed = IsComputed((int)s.hdb_site_datatype_id);

                DataTable tbl = Table(s.hdb_site_datatype_id,
                  s.hdb_r_table, s.Interval,gd.GraphRow.InstantInterval,
                  gd.BeginingTime(),
                  gd.EndingTime(), gd.GraphRow.TimeZone);

                s.ReadOnly = ReadOnly((int)s.hdb_site_datatype_id);
                
                tbl.TableName = "table" + idx;
                s.TableName = tbl.TableName;
                tbl.DataSet.Tables.Remove(tbl);

                gd.Tables.Add(tbl);
                idx++;
            }

            gd.BuildIntervalTables();
        }

        
        
         public DataTable ObjectTypesTable
        {
            get
            {
                if (_object_types == null)
                {
                   string s= ConfigurationManager.AppSettings["objectTypes"];
                    string[] otypes = s.Split(',');
                    string sql = "select * from hdb_objecttype ";
                    sql += "Where objecttype_name in ('"
                        + String.Join("','", otypes) + "') ";
                    sql += "order by objecttype_name";
                    _object_types = m_server.Table("hdb_objecttype", sql);
                }
                return _object_types;
            }
        }

        //public static

        public DataSet BuildSiteList()
        {
            string filename = "SiteList.xml";
            string[] tables =
			{
				"hdb_site_datatype",
				"hdb_site",
				"hdb_datatype",
				"hdb_unit",
				"ref_hm_site_pcode",
				"ref_hm_site",
				"ref_hm_filetype",
				"hdb_objecttype"
			};

            DataSet ds = new DataSet("SiteList");
            if (File.Exists(filename))
            {
                ds.ReadXml(filename, XmlReadMode.ReadSchema);
            }
            for (int i = 0; i < tables.Length; i++)
            {
                if (ds.Tables.Contains(tables[i]))
                    continue;

                DataTable t = m_server.Table(tables[i], "select * from " + tables[i]);
                t.DataSet.Tables.Remove(t);
                ds.Tables.Add(t);
            }
            //ds.WriteXml(filename,XmlWriteMode.WriteSchema);
            return ds;

        }


        public DataTable SiteList(string siteSearchString, int[] objecttype_id)
        {
            siteSearchString = SafeSqlLikeClauseLiteral(siteSearchString);
            string sql = "select c.site_id, c.site_common_name, c.objecttype_id,d.objecttype_name,e.state_name from "
              + " hdb_site c, hdb_objecttype d, hdb_state e "
              + "where c.state_id = e.state_id (+)  and "
              + " d.objecttype_id = c.objecttype_id";

            if (siteSearchString.Trim() != "")
            {
                sql += " and lower(c.site_common_name) like '%" + siteSearchString.ToLower().Trim() + "%'";
            }
            if (objecttype_id.Length > 0)
            {
                sql += " and (";
                for (int i = 0; i < objecttype_id.Length; i++)
                {
                    sql += " c.objecttype_id = " + objecttype_id[i];
                    if (i < objecttype_id.Length - 1)
                    {
                        sql += " or ";
                    }
                }
                sql += " ) ";
            }

            sql += " order by site_common_name";
            DataTable rval = m_server.Table("SiteList", sql);

            return rval;
        }

        /// <summary>
        /// Gets list of sites for the graph series editor (graph properties)
        /// </summary>
        public DataTable FilteredSiteList(string siteSearchString,
                                               string[] r_names,
                                               int[] objecttype_id,
                                               string basin_id)
        {

            siteSearchString = SafeSqlLikeClauseLiteral(siteSearchString);
            string sql_template = "select c.site_id, c.site_common_name, c.objecttype_id from #TABLE_NAME# a, "
              + "hdb_site_datatype b, hdb_site c, hdb_objecttype d "
              + "where a.site_datatype_id = b.site_datatype_id  and "
              + "b.site_id = c.site_id and d.objecttype_id = c.objecttype_id";
            if (basin_id!= "-1")
            {
                sql_template += " and c.basin_id = "+basin_id;
            }

            if (siteSearchString.Trim() != "")
            {
                sql_template += " and lower(c.site_common_name) like '%" + siteSearchString.ToLower().Trim() + "%'";
            }
            if (objecttype_id.Length > 0)
            {
                sql_template += " and (";
                for (int i = 0; i < objecttype_id.Length; i++)
                {
                    sql_template += " c.objecttype_id = " + objecttype_id[i];
                    if (i < objecttype_id.Length - 1)
                    {
                        sql_template += " or ";
                    }
                }
                sql_template += " ) ";
            }


            if (r_names.Length == 1)
            {
                sql_template = sql_template.Replace("select ", "select distinct ");
            }
            string sql = "";
            for (int i = 0; i < r_names.Length; i++)
            {
                int idx = Array.IndexOf(Hdb.r_names, r_names[i]);
                Debug.Assert(idx >= 0);
                string tableName = Hdb.r_tables[idx];
                sql += sql_template;
                sql = sql.Replace("#TABLE_NAME#", tableName);

                if (i < r_names.Length - 1)
                {// append union keyword
                    sql += " UNION \n";
                }
            }
            sql += " order by site_common_name";
            DataTable rval = m_server.Table("SiteList", sql);

            //       DataTable t =  HDB.HDB_objecttype;



            return rval;
        }

        /// <summary>
        /// http://msdn.microsoft.com/library/default.asp?url=/library/en-us/dnnetsec/html/SecNetch12.asp
        /// </summary>
        /// <param name="inputSQL"></param>
        /// <returns></returns>
        static string SafeSqlLikeClauseLiteral(string inputSQL)
        {
            // Make the following replacements:
            // '  becomes  ''
            // [  becomes  [[]
            // %  becomes  [%]
            // _  becomes  [_]

            string s = inputSQL;
            s = inputSQL.Replace("'", "''");
            //s = s.Replace("[", "[[]");
            //s = s.Replace("%", "[%]");
            //s = s.Replace("_", "_");
            return s;
        }  


        #region // sql to get info for a specific site, including period of record.
        /*
   select 'INSTANT' interval,d.datatype_id, d.datatype_common_name,
count(a.value), min(start_date_time), max(start_date_time)
from
r_instant a, hdb_site_datatype b, hdb_datatype d
where
a.site_datatype_id = b.site_datatype_id and
b.datatype_id = d.datatype_id and
b.site_id = 919
group by d.datatype_id, d.datatype_common_name
UNION
select 'Hour' interval,d.datatype_id, d.datatype_common_name,
count(a.value), min(start_date_time), max(start_date_time)
from
r_hour a, hdb_site_datatype b, hdb_datatype d
where
a.site_datatype_id = b.site_datatype_id and
b.datatype_id = d.datatype_id and
b.site_id = 919
group by d.datatype_id, d.datatype_common_name
UNION

select 'DAY' interval,d.datatype_id, d.datatype_common_name,
count(a.value), min(start_date_time), max(start_date_time)
from
r_day a, hdb_site_datatype b, hdb_datatype d
where
a.site_datatype_id = b.site_datatype_id and
b.datatype_id = d.datatype_id and
b.site_id = 919
group by d.datatype_id, d.datatype_common_name
UNION
select 'MONTH' interval, d.datatype_id, d.datatype_common_name,
count(a.value), min(start_date_time), max(start_date_time)
from
r_month a, hdb_site_datatype b, hdb_datatype d
where
a.site_datatype_id = b.site_datatype_id and
b.datatype_id = d.datatype_id and
b.site_id = 919 
group by d.datatype_id, d.datatype_common_name
UNION
select 'YEAR' interval,d.datatype_id, d.datatype_common_name,
count(a.value), min(start_date_time), max(start_date_time)
from
r_year a, hdb_site_datatype b, hdb_datatype d
where
a.site_datatype_id = b.site_datatype_id and
b.datatype_id = d.datatype_id and
b.site_id = 919
group by d.datatype_id, d.datatype_common_name
UNION
select 'WATER YEAR' interval,d.datatype_id, d.datatype_common_name,
count(a.value), min(start_date_time), max(start_date_time)
from
r_wy a, hdb_site_datatype b, hdb_datatype d
where
a.site_datatype_id = b.site_datatype_id and
b.datatype_id = d.datatype_id and
b.site_id = 919
group by d.datatype_id, d.datatype_common_name
;*/
        #endregion
        /// <summary>
        /// SiteInfo returns a list of data types and the period of record for
        /// each data type.  For example the following table is returned for site 919
        /// Connected.
        ///
        ///   INTERVA DATATYPE_ID DATATYPE_COMMON_NAME                                             COUNT(A.VALUE) rtable  site_datatype_id    site_id MIN(START MAX(START
        ///   ------- ----------- ---------------------------------------------------------------- -------------- ------- ---------------- ---------- --------- ---------
        ///    daily            17 reservoir storage, end of period reading used as value for per.            5046 r_day               1730        932 01-OCT-70 08-JUL-03
        ///    daily            29 average inflow                                                             4553 r_day               1801        932 02-OCT-90 08-JUL-03
        ///    daily            42 average sum of all reservoir releases (pwr and oth)                        4552 r_day               1881        932 02-OCT-90 08-JUL-03
        ///    daily            49 reservoir WS elevation, end of per reading used as value for per           5044 r_day               1937        932 01-OCT-70 08-JUL-03
        ///    monthly          17 reservoir storage, end of period reading used as value for per.             389 r_month             1730        932 01-OCT-70 01-FEB-03
        ///    monthly          49 reservoir WS elevation, end of per reading used as value for per            120 r_month             1937        932 01-OCT-70 01-JUL-86
        /// </summary>
        public DataTable SiteInfo(int site_id, string[] r_names, bool showBase)
        {

            string sql_template =
                         "  select '#RNAMES1#' interval,#RNAMES2# interval_Text ,d.datatype_id, d.datatype_common_name, "
                      + " count(a.value),'#TABLE_NAME#' \"rtable\", max(a.site_datatype_id) "
                      + " \"site_datatype_id\" ,max(b.site_id) \"site_id\", min(start_date_time), "
                      + " max(start_date_time), max(e.unit_common_name) \"unit_common_name\", max(c.site_common_name) \"site_common_name\"  "
                      + " from "
                      + " #TABLE_NAME# a, hdb_site_datatype b, hdb_site c, hdb_datatype d, hdb_unit e "
                      + " where "
                      + " a.site_datatype_id = b.site_datatype_id and "
                      + " b.datatype_id = d.datatype_id and c.site_id =" + site_id.ToString()
                      + "  and e.unit_id = d.unit_id and "
                      + " b.site_id = " + site_id.ToString()
                      + " group by d.datatype_id, d.datatype_common_name ";
            string sql = "";
            for (int i = 0; i < r_names.Length; i++)
            {
                int idx = Array.IndexOf(Hdb.r_names, r_names[i]);
                Debug.Assert(idx >= 0);
                string tableName = Hdb.r_tables[idx];
                string query = sql_template;
                query = query.Replace("#TABLE_NAME#", tableName);
                query = query.Replace("#RNAMES1#", Hdb.r_names[idx]);
                query = query.Replace("#RNAMES2#","'"+ Hdb.r_names[idx] +"'");
               
                if (i < r_names.Length - 1)
                {// append union keyword
                    query += " UNION \n";
                }
                sql += query;
            }

        
                if (showBase)
                {

                    string query = sql_template;
                    query = query.Replace("#TABLE_NAME#", "r_base");
                    query = query.Replace("'#RNAMES1#'", "interval");
                    query = query.Replace("#RNAMES2#", "interval ||' - base' ");


                    string where = " and interval in ('"
                        + String.Join("','", r_names) + "') ";
                    query = query.Replace("group by d.datatype_id,",
                        where +"group by interval,d.datatype_id,");

                    sql += " UNION \n";
                    sql += query;
                }
               
            DataTable rval = m_server.Table("SiteInfo", sql);
    

            return rval;

        }

        /// <summary>
        /// Determine if any values are computed (Yellow) for this interval
        /// </summary>
        internal static bool AnyComputedValues(GraphData ds, string interval)
        {
            foreach (var s in ds.SeriesRows)
            {
                string tableName = s.TableName;
                if (String.Compare(s.Interval, interval, true) == 0)
                {
                    DataTable tbl = ds.GetTable(tableName);
                    string sql = "SourceCOLOR ='Yellow'";
                    DataRow[] rows = tbl.Select(sql, "", DataViewRowState.ModifiedCurrent);
                    return rows.Length > 0;
                }
            }
            return false;
        }


        internal string[] AclGroupNames()
        {
            string sql = "select distinct group_name from ref_user_groups order by group_name";
            DataTable tbl = Server.Table("a", sql);

            var userList = (from row in tbl.AsEnumerable()
                            select row.Field<string>("group_name")
                             ).ToArray();
            return userList;

        }

        internal DataTable BasinNames()
        {
         //   string sql = "select site_name,site_id from hdb_site where objecttype_id = 1 order by site_name ";
            string sql = "  select hs.site_name,site_id "
                        + " from hdb_site hs, ref_db_list rdl "
                    + "  where hs.objecttype_id = 1                    "
                    + "  and hs.db_site_code = rdl.db_site_code        "
                    + "  and rdl.session_no = 1                        "
                    + "    order by site_name                          ";

            var rval = Server.Table("a", sql);

            var r = rval.NewRow();
            r[0]="All";
            r[1] =-1;
            rval.Rows.InsertAt(r, 0);

            return rval;
            //var basins = from row in tbl.AsEnumerable()
            //                select row.Field<string>("site_name")
            //                 ).ToArray();
            //return basins;

        }
        /// <summary>
        /// 
        /// </summary>
        /// <param name="t"></param>
        /// <param name="site_datatype_id"></param>
        /// <param name="interval">instant,hour,day,month</param>
        /// <returns></returns>
        internal DataTable BaseInfo(DateTime t, decimal site_datatype_id, string interval)
        {
            string sql = "select site_datatype_id, interval, start_date_time,end_date_time, value, agen_name, overwrite_flag, "
+"  r_base.date_time_loaded, validation, collection_system_name, "
+" loading_application_name, method_name, computation_name, data_flags from "
+" r_base, hdb_agen, hdb_collection_system, hdb_loading_application, hdb_method, cp_computation where "
+" site_datatype_id = "+ site_datatype_id+ "and "
+" start_date_time = "+ToHdbDate(t)+" and "
+" interval = '"+interval+"' and "
+" hdb_agen.agen_id = r_base.agen_id and "
+" hdb_collection_system.collection_system_id = r_base.collection_system_id and "
+" hdb_loading_application.loading_application_id = r_base.loading_application_id and "
+" hdb_method.method_id = r_base.method_id and "
+" cp_computation.computation_id = r_base.computation_id ";

            return Server.Table("info", sql);
         }
        
    }
}
