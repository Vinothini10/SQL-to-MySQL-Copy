﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using System.IO;
using MySql.Data.MySqlClient;
using System.Data.SqlClient;
using System.Threading;
using System.Text.RegularExpressions;

namespace SQLToMySQL
{
    public partial class SQLToMySQL : Form
    {

        public SQLToMySQL(string[] args)
        {
            InitializeComponent();
            ct.Text = "";
            status.Text = "";

            if (args.Length > 0 && args[0].Trim() != "") button1_Click(null, null);
        }

        private long threadLock = 0, threadError = 0;
        private void button1_Click(object sender, EventArgs e)
        {
            button1.Enabled = false;
            Properties.Settings.Default.Save();


            try
            {
                bool go = true;
                while (go)
                {
                    using (SqlConnection sourceCon = new SqlConnection("Server=" + serverSource.Text + ";Initial Catalog=" + dbSource.Text + (userSource.Text == "" ? ";Trusted_Connection=True;" : ";Persist Security Info=False;User ID=" + userSource.Text + ";Password=" + passSource.Text + ";Connection Timeout=30;")))
                    {
                        sourceCon.Open();
                        using (MySqlConnection con = new MySqlConnection(@"server=" + server.Text + ";uid=" + user.Text + ";pwd=" + pass.Text + ";database=" + db.Text + ";port=3306"))
                        {
                            con.Open();

                            if (clear.Checked)
                            {
                                status.Text = "Clearing dest. table...";
                                using (MySqlCommand cmd = new MySqlCommand())
                                {
                                    cmd.Connection = con;
                                    cmd.CommandText = "TRUNCATE TABLE " + table.Text;
                                    cmd.ExecuteNonQuery();
                                }
                                status.Text = "Dest. table cleared.";
                            }

                            List<string> rows = new List<string>();
                            DataTable t = new System.Data.DataTable();
                            int i = 0;

                            using (SqlCommand com = new SqlCommand(tableSource.Text, sourceCon))
                            {
                                com.CommandTimeout = 3600;
                                using (SqlDataReader r = com.ExecuteReader())
                                {
                                    while (r.Read())
                                    {
                                        i++;
                                        status.Text = "Reading...";
                                        ct.Text = "Row " + i;
                                        if (i % 10000 == 0) Application.DoEvents();

                                        if (i == 1)
                                        {
                                            for (int x = 0; x < r.FieldCount; x++)
                                                t.Columns.Add(r.GetName(x), r.GetFieldType(x));
                                        }

                                        object[] cells = new object[r.FieldCount];
                                        for (int x = 0; x < r.FieldCount; x++)
                                            cells[x] = r.GetValue(x);

                                        t.Rows.Add(cells);

                                        if (t.Rows.Count >= 100000)
                                        {
                                            Application.DoEvents();
                                            status.Text = "Inserting...";
                                            Interlocked.Increment(ref threadLock);
                                            ThreadPool.QueueUserWorkItem(new WaitCallback(Insert), new object[] { con, table.Text, t, update.Checked });
                                            while (threadLock > 0)
                                            {
                                                Thread.Sleep(1000);
                                                Application.DoEvents();
                                            }
                                            if (threadError > 0)
                                            {
                                                status.Text = "Errored.";
                                                button1.Enabled = true;
                                                return;
                                            }
                                        }
                                    }
                                }
                            }
                            if (t.Rows.Count > 0)
                            {
                                Application.DoEvents();
                                status.Text = "Inserting...";
                                Interlocked.Increment(ref threadLock);
                                ThreadPool.QueueUserWorkItem(new WaitCallback(Insert), new object[] { con, table.Text, t, update.Checked });
                                while (threadLock > 0)
                                {
                                    Thread.Sleep(1000);
                                    Application.DoEvents();
                                }
                                if (threadError > 0)
                                {
                                    status.Text = "Errored.";
                                    button1.Enabled = true;
                                    return;
                                }
                            }

                            con.Close();
                        }
                        sourceCon.Close();
                    }
                    status.Text = "Done!";
                    if (!loop.Checked)
                    {
                        go = false;

                    }
                    else
                    {
                        DateTime now = DateTime.Now;
                        DateTime waitUntil = DateTime.Now.AddMinutes(1);
                        while (now <= waitUntil)
                        {
                            Application.DoEvents();
                            Thread.Sleep(1000);
                            now = DateTime.Now;
                            status.Text = "" + (waitUntil - now).TotalSeconds.ToString("N0");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error: " + ex);
                status.Text = "Errored.";
            }
            button1.Enabled = true;
        }

        private void Insert(object o)
        {
            try
            {
                MySqlConnection con = ((MySqlConnection)((object[])o)[0]);
                string table = (string)((object[])o)[1];
                DataTable t = (DataTable)((object[])o)[2];
                bool updateIfExists = (bool)((object[])o)[3];
                if (updateIfExists)
                {
                    using (MySqlCommand cmd = new MySqlCommand())
                    {
                        cmd.Connection = con;
                        string update = "";
                        string columnnames = "";
                        foreach (DataColumn c in t.Columns)
                        {
                            if (columnnames != "") columnnames += ", ";
                            columnnames += c.ColumnName;
                        }
                        foreach (DataRow r in t.Rows)
                        {
                            string v = "";
                            int i = 0;
                            foreach (object val in r.ItemArray)
                            {
                                if (v != "") v += ", ";
                                v += "'" + val.ToString() + "'";

                                if (i > 0)
                                {
                                    if (update != "") update += ", ";
                                    update += t.Columns[i] + "='" + val.ToString() + "'";
                                }
                                i++;
                            }
                            cmd.CommandText = "INSERT INTO " + table + " (" + columnnames + @")
        VALUES (" + v + @")
        ON DUPLICATE KEY UPDATE 
          " + update;
                            try
                            {
                                //WriteLine(cmd.CommandText);
                                cmd.ExecuteNonQuery();
                            }
                            catch { MessageBox.Show("Error for: " + cmd.CommandText); }
                        }
                    }
                }
                else
                {
                    BulkInsertMySQL(t, table, con);
                }
                t.Rows.Clear();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error on insert: " + ex);
                Interlocked.Increment(ref threadError);
            }
            Interlocked.Decrement(ref threadLock);
        }

        public DataTable GetDataTableLayout(string tableName, MySqlConnection connection)
        {
            DataTable table = new DataTable();


            connection.Open();
            // Select * is not a good thing, but in this cases is is very usefull to make the code dynamic/reusable 
            // We get the tabel layout for our DataTable
            string query = "SELECT * FROM " + tableName + " limit 0";
            using (MySqlDataAdapter adapter = new MySqlDataAdapter(query, connection))
            {
                adapter.Fill(table);
            };
            return table;
        }

        public void BulkInsertMySQL(DataTable table, string tableName, MySqlConnection connection)
        {

            using (MySqlTransaction tran = connection.BeginTransaction(IsolationLevel.Serializable))
            {
                using (MySqlCommand cmd = new MySqlCommand())
                {
                    cmd.Connection = connection;
                    cmd.Transaction = tran;
                    cmd.CommandText = "SELECT * FROM " + tableName + " limit 0";

                    using (MySqlDataAdapter adapter = new MySqlDataAdapter(cmd))
                    {
                        adapter.UpdateBatchSize = 10000;
                        using (MySqlCommandBuilder cb = new MySqlCommandBuilder(adapter))
                        {
                            cb.SetAllValues = true;
                            adapter.Update(table);
                            tran.Commit();
                        }
                    };
                }
            }
        }

        static void WriteLine(string s, bool log = true)
        {
            s = DateTime.Now + ": " + s;
            Console.WriteLine(s);
            if (log) File.AppendAllLines("SqlToMySql_log.txt", new string[] { s });
        }
    }
}