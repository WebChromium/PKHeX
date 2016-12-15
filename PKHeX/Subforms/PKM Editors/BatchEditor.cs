﻿using PKHeX.Reflection;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace PKHeX
{
    public partial class BatchEditor : Form
    {
        public BatchEditor()
        {
            InitializeComponent();
            DragDrop += tabMain_DragDrop;
            DragEnter += tabMain_DragEnter;

            CB_Format.Items.Clear();
            CB_Format.Items.Add("All");
            foreach (Type t in types) CB_Format.Items.Add(t.Name.ToLower());
            CB_Format.Items.Add("Any");

            CB_Format.SelectedIndex = CB_Require.SelectedIndex = 0;
        }
        private static string[][] getPropArray()
        {
            var p = new string[types.Length][];
            for (int i = 0; i < p.Length; i++)
                p[i] = ReflectUtil.getPropertiesCanWritePublic(types[i]).ToArray();

            IEnumerable<string> all = p.SelectMany(prop => prop).Distinct();
            IEnumerable<string> any = p[0];
            for (int i = 1; i < p.Length; i++)
                any = any.Union(p[i]);

            var p1 = new string[types.Length + 2][];
            Array.Copy(p, 0, p1, 1, p.Length);
            p1[0] = all.ToArray();
            p1[p1.Length-1] = any.ToArray();

            return p1;
        }

        private const string CONST_RAND = "$rand";
        private const string CONST_SHINY = "$shiny";
        private int currentFormat = -1;
        private static readonly Type[] types = {typeof (PK7), typeof (PK6), typeof (PK5), typeof (PK4), typeof (PK3)};
        private static readonly string[][] properties = getPropArray();

        // GUI Methods
        private void B_Open_Click(object sender, EventArgs e)
        {
            if (!B_Go.Enabled) return;
            var fbd = new FolderBrowserDialog();
            if (fbd.ShowDialog() != DialogResult.OK)
                return;

            TB_Folder.Text = fbd.SelectedPath;
            TB_Folder.Visible = true;
        }
        private void B_SAV_Click(object sender, EventArgs e)
        {
            TB_Folder.Text = "";
            TB_Folder.Visible = false;
        }
        private void B_Go_Click(object sender, EventArgs e)
        {
            if (b.IsBusy)
            { Util.Alert("Currently executing instruction list."); return; }

            if (RTB_Instructions.Lines.Any(line => line.Length == 0))
            { Util.Error("Line length error in instruction list."); return; }

            runBackgroundWorker();
        }

        private BackgroundWorker b = new BackgroundWorker { WorkerReportsProgress = true };
        private void runBackgroundWorker()
        {
            var Filters = ReflectUtil.getFilters(RTB_Instructions.Lines).ToList();
            if (Filters.Any(z => string.IsNullOrWhiteSpace(z.PropertyValue)))
            { Util.Error("Empty Filter Value detected."); return; }

            var Instructions = ReflectUtil.getInstructions(RTB_Instructions.Lines).ToList();
            var emptyVal = Instructions.Where(z => string.IsNullOrWhiteSpace(z.PropertyValue)).ToArray();
            if (emptyVal.Any())
            {
                string props = string.Join(", ", emptyVal.Select(z => z.PropertyName));
                if (DialogResult.Yes != Util.Prompt(MessageBoxButtons.YesNo, 
                    $"Empty Property Value{(emptyVal.Length > 1 ? "s" : "")} detected:" + Environment.NewLine + props,
                    "Continue?"))
                    return;
            }

            string destPath = "";
            if (RB_Path.Checked)
            {
                Util.Alert("Please select the folder where the files will be saved to.", "This can be the same folder as the source of PKM files.");
                var fbd = new FolderBrowserDialog();
                var dr = fbd.ShowDialog();
                if (dr != DialogResult.OK)
                    return;

                destPath = fbd.SelectedPath;
            }

            FLP_RB.Enabled = RTB_Instructions.Enabled = B_Go.Enabled = false;

            b = new BackgroundWorker {WorkerReportsProgress = true};

            b.DoWork += (sender, e) => {
                if (RB_SAV.Checked)
                {
                    var data = Main.SAV.BoxData;
                    setupProgressBar(data.Length);
                    processSAV(data, Filters, Instructions);
                }
                else
                {
                    var files = Directory.GetFiles(TB_Folder.Text, "*", SearchOption.AllDirectories);
                    setupProgressBar(files.Length);
                    processFolder(files, Filters, Instructions, destPath);
                }
            };
            b.ProgressChanged += (sender, e) =>
            {
                setProgressBar(e.ProgressPercentage);
            };
            b.RunWorkerCompleted += (sender, e) => {
                string result = $"Modified {ctr}/{len} files.";
                if (err > 0)
                    result += Environment.NewLine + $"{err} files ignored due to an internal error.";
                Util.Alert(result);
                FLP_RB.Enabled = RTB_Instructions.Enabled = B_Go.Enabled = true;
                setupProgressBar(0);
            };
            b.RunWorkerAsync();
        }

        // Progress Bar
        private void setupProgressBar(int count)
        {
            MethodInvoker mi = () => { PB_Show.Minimum = 0; PB_Show.Step = 1; PB_Show.Value = 0; PB_Show.Maximum = count; };
            if (PB_Show.InvokeRequired)
                PB_Show.Invoke(mi);
            else
                mi.Invoke();
        }
        private void setProgressBar(int i)
        {
            if (PB_Show.InvokeRequired)
                PB_Show.Invoke((MethodInvoker)(() => PB_Show.Value = i));
            else { PB_Show.Value = i; }
        }
        
        // Mass Editing
        private int ctr, len, err;
        private void processSAV(PKM[] data, List<BatchEditorStringInstruction> Filters, List<BatchEditorStringInstruction> Instructions)
        {
            len = err = ctr = 0;
            for (int i = 0; i < data.Length; i++)
            {
                var pkm = data[i];
                if (!pkm.Valid)
                {
                    b.ReportProgress(i);
                    continue;
                }

                BatchEditorModifyResult r = ReflectUtil.ProcessPKM(pkm, Filters, Instructions);
                if (r != BatchEditorModifyResult.Invalid)
                    len++;
                if (r == BatchEditorModifyResult.Error)
                    err++;
                if (r == BatchEditorModifyResult.Modified)
                {
                    if (pkm.Species != 0)
                        pkm.RefreshChecksum();
                    ctr++;
                }

                b.ReportProgress(i);
            }

            Main.SAV.BoxData = data;
        }
        private void processFolder(string[] files, List<BatchEditorStringInstruction> Filters, List<BatchEditorStringInstruction> Instructions, string destPath)
        {
            len = err = ctr = 0;
            for (int i = 0; i < files.Length; i++)
            {
                string file = files[i];
                if (!PKX.getIsPKM(new FileInfo(file).Length))
                {
                    b.ReportProgress(i);
                    continue;
                }

                byte[] data = File.ReadAllBytes(file);
                var pkm = PKMConverter.getPKMfromBytes(data);
                
                if (!pkm.Valid)
                {
                    b.ReportProgress(i);
                    continue;
                }

                BatchEditorModifyResult r = ReflectUtil.ProcessPKM(pkm, Filters, Instructions);
                if (r != BatchEditorModifyResult.Invalid)
                    len++;
                if (r == BatchEditorModifyResult.Error)
                    err++;
                if (r == BatchEditorModifyResult.Modified)
                {
                    if (pkm.Species > 0)
                    {
                        pkm.RefreshChecksum();
                        File.WriteAllBytes(Path.Combine(destPath, Path.GetFileName(file)), pkm.DecryptedBoxData);
                        ctr++;
                    }
                }

                b.ReportProgress(i);
            }
        }
        
        private void tabMain_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
        }
        private void tabMain_DragDrop(object sender, DragEventArgs e)
        {
            string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (!Directory.Exists(files[0])) return;

            TB_Folder.Text = files[0];
            TB_Folder.Visible = true;
            RB_SAV.Checked = false;
            RB_Path.Checked = true;
        }
        private void CB_Property_SelectedIndexChanged(object sender, EventArgs e)
        {
            L_PropType.Text = getPropertyType(CB_Property.Text);
        }
        private string getPropertyType(string propertyName)
        {
            int typeIndex = CB_Format.SelectedIndex;
            
            if (typeIndex == 0) // All
                return types[0].GetProperty(propertyName).PropertyType.Name;

            if (typeIndex == properties.Length - 1) // Any
                foreach (var p in types.Select(t => t.GetProperty(propertyName)).Where(p => p != null))
                    return p.PropertyType.Name;
            
            return types[typeIndex - 1].GetProperty(propertyName).PropertyType.Name;
        }

        private void B_Add_Click(object sender, EventArgs e)
        {
            if (CB_Property.SelectedIndex < 0)
            { Util.Alert("Invalid property selected."); return; }

            char[] prefix = {'.', '=', '!'};
            string s = prefix[CB_Require.SelectedIndex] + CB_Property.Items[CB_Property.SelectedIndex].ToString() + "=";
            if (RTB_Instructions.Lines.Length != 0 && RTB_Instructions.Lines.Last().Length > 0)
                s = Environment.NewLine + s;

            RTB_Instructions.AppendText(s);
        }

        private void CB_Format_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (currentFormat == CB_Format.SelectedIndex)
                return;

            int format = CB_Format.SelectedIndex;
            CB_Property.Items.Clear();
            CB_Property.Items.AddRange(properties[format]);
            CB_Property.SelectedIndex = 0;
            currentFormat = format;
        }
    }
}
