﻿using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Controls;
using FakeChan22.Tasks;
using System.Runtime.InteropServices;
using System.Runtime.Serialization.Json;
using System.Reflection;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FakeChan22.Params;
using FakeChan22.Config;
using FakeChan22.Configs;

namespace FakeChan22
{
    /// <summary>
    /// MainWindow.xaml の相互作用ロジック
    /// </summary>
    public partial class MainWindow : Window
    {
        private string versionStr = "1.0.12";

        /// <summary>
        /// アプリ全体の設定格納
        /// </summary>
        FakeChanConfig config;

        /// <summary>
        /// リスナタスク管理
        /// </summary>
        TaskManager taskManager;

        /// <summary>
        /// 同期発声用のメッセージキュー
        /// </summary>
        MessageQueueWrapper messageQueue = new MessageQueueWrapper();

        /// <summary>
        /// AssistantSeikaとの通信用オブジェクト
        /// </summary>
        ScAPIs api = null;

        /// <summary>
        /// シリアライズ/デシリアライズ用の型コレクション
        /// </summary>
        FakeChanTypesCollector TypeCollection = new FakeChanTypesCollector();

        /// <summary>
        /// Win32との相互運用で使用
        /// </summary>
        WindowInteropHelper WinHelper;

        public MainWindow()
        {

            // 古いバージョンの設定のバージョンアップを試みる
            if (Properties.Settings.Default.UpgradeRequired)
            {
                Properties.Settings.Default.Upgrade();
                Properties.Settings.Default.UpgradeRequired = false;
                Properties.Settings.Default.Save();
            }

            if (Properties.Settings.Default.UserDatas != "")
            {
                // 取り外された Task への対応

                JsonNode configNode = JsonNode.Parse(Properties.Settings.Default.UserDatas);
                List<JsonNode> nodes = configNode["listenerConfigLists"].AsArray().ToList<JsonNode>();

                foreach (var item in nodes)
                {
                    string lsnrTypeName = Regex.Replace(item[@"__type"].GetValue<string>(), @"^([^:#]+):#([^:#]+)$", @"$2.$1");

                    if (!TypeCollection.ListenerConfigTypeDictionary.ContainsKey(lsnrTypeName))
                    {
                        JsonNode tNode = item as JsonNode;
                        (configNode["listenerConfigLists"] as JsonArray).Remove(tNode);
                    }
                }

                // 改めて保存された設定を読み込む

                DataContractJsonSerializerSettings settings = new DataContractJsonSerializerSettings();
                settings.KnownTypes = TypeCollection.ListenerConfigTypeDictionary.Values.Select(v => v).ToList();

                DataContractJsonSerializer uds = new DataContractJsonSerializer(typeof(FakeChanConfig), settings);
                MemoryStream ms = new MemoryStream(Encoding.UTF8.GetBytes(configNode.ToJsonString()));
                config = (FakeChanConfig)uds.ReadObject(ms);
                ms.Close();
            }
            else
            {
                config = new FakeChanConfig();
            }

            config.RebuildReplaceDefinitionList();
            config.RebuildSoloSpeechList();
            config.RebuildQueueParam();

            config.versionStr = this.versionStr;

            InitializeComponent();

            ComboBoxSpeakerLists.ItemsSource = null;
            ComboBoxSpeakerLists.ItemsSource = config.speakerLists;

            ComboBoxRegexDefinitionLists.ItemsSource = null;
            ComboBoxRegexDefinitionLists.ItemsSource = config.replaceDefinitionLists;

            ComboBoxListenerConfigLists.ItemsSource = null;
            ComboBoxListenerConfigLists.ItemsSource = config.listenerConfigLists;

            // ウインドウタイトル変更
            this.Title = config.fakeChan22WindowTitle + " " + config.versionStr;
            TextBoxWinTitle.Text = config.fakeChan22WindowTitle;

            // comment.xml 生成パス設定
            if ((config.commentXmGenlPath == "") || (config.commentXmGenlPath == @".\"))
            {
                config.commentXmGenlPath = Directory.GetParent(Assembly.GetExecutingAssembly().Location).FullName;
            }
            TextBoxCommentXmlPath.Text = config.commentXmGenlPath;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            WinHelper = new WindowInteropHelper(this);
            HwndSource MsgProc = HwndSource.FromHwnd(WinHelper.Handle);
            MsgProc.AddHook(WndProc);


            try
            {
                api = new ScAPIs();
            }
            catch(Exception e1)
            {
                MessageBox.Show(String.Format(@"AssistantSeikaとの接続ができません : {0}", e1.Message), "接続処理");
                Application.Current.Shutdown();
            }

            try
            {
                int count = api.AvatorList().Count;

                if(count==0)
                {
                    MessageBox.Show(String.Format(@"AssistantSeikaで認識されている話者が存在しません"), "接続処理");
                    Application.Current.Shutdown();
                }
            }
            catch (Exception e2)
            {
                MessageBox.Show(String.Format(@"AssistantSeikaの製品スキャンが実行されていない可能性があります : {0}", e2.Message), "接続処理");
                Application.Current.Shutdown();
            }

            // 話者リストが無い時は強制的に作成させる
            if (config.speakerLists == null) config.speakerLists = new List<SpeakerFakeChanList>();
            if (config.speakerLists.Count == 0)
            {
                MessageBox.Show("話者リストの登録がないので最初に作成してください", "初期設定処理");

                CreateNewSpeakerList();
            }
            else
            {
                // 念のため、話者のマップ再構築実行
                foreach(var item in config.speakerLists)
                {
                    item.MakeValidObjects();
                }
            }

            config.RebuildListenerConfig(this.TypeCollection);
            config.RebuildMappingObjects();

            ComboBoxSpeakerLists.SelectedIndex = 0;

            ComboBoxRegexDefinitionLists.SelectedIndex = 0;

            ComboBoxListenerConfigLists.SelectedIndex = 0;

            // バックグラウンドタスク管理
            taskManager = new TaskManager(ref config.listenerConfigLists, ref messageQueue, ref config, ref TypeCollection);

            taskManager.ClipboardTask.OnSetClipboardChain += SetClipboardListener;
            taskManager.ClipboardTask.OnRemoveClipboardChain += RemoveClipboardListener;

            taskManager.OnLogging += Logging;
            taskManager.TaskBoot();

        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            taskManager.TaskShutdown();

            DataContractJsonSerializerSettings settings = new DataContractJsonSerializerSettings();
            settings.KnownTypes = TypeCollection.ListenerConfigTypeDictionary.Values.Select(v => v).ToList(); // new List<Type>();

            DataContractJsonSerializer uds = new DataContractJsonSerializer(typeof(FakeChanConfig), settings);
            MemoryStream ms = new MemoryStream();
            uds.WriteObject(ms, config);

            Properties.Settings.Default.UserDatas = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)(ms.Length));
            Properties.Settings.Default.Save();
            ms.Close();
        }

        /// <summary>
        /// 話者リストの新規追加処理
        /// </summary>
        private void CreateNewSpeakerList()
        {
            var ss = new SpeakerFakeChanList();
            Window wd = new EditSpeakerList(ref ss);

            wd.ShowDialog();
            config.speakerLists.Add(ss);
            ComboBoxSpeakerLists.ItemsSource = null;
            ComboBoxSpeakerLists.ItemsSource = config.speakerLists;
            ComboBoxSpeakerLists.SelectedItem = ss;
        }

        /// <summary>
        /// 話者リストの更新処理
        /// </summary>
        private void UpdateSpeakerList()
        {
            var ss = ComboBoxSpeakerLists.SelectedItem as SpeakerFakeChanList;

            Window wd = new EditSpeakerList(ref ss);

            wd.ShowDialog();
            ComboBoxSpeakerLists.ItemsSource = null;
            ComboBoxSpeakerLists.ItemsSource = config.speakerLists;
            ComboBoxSpeakerLists.SelectedItem = ss;
        }

        /// <summary>
        /// 話者リスト利用確認
        /// </summary>
        /// <param name="ss">利用確認したい話者リスト</param>
        /// <returns>利用中の時はtrue</returns>
        private bool IsUsedSpeakerList(ref SpeakerFakeChanList ss)
        {
            bool ans = false;

            foreach(var item in config.listenerConfigLists)
            {
                if (item.SpeakerListDefault.Equals(ss))
                {
                    ans = true;
                    break;
                }
                if (item.SpeakerListNoJapaneseJudge.Equals(ss))
                {
                    ans = true;
                    break;
                }
            }

            if (ans) return ans;

            foreach(var item in config.soloSpeechList.SpeechDefinitions)
            {
                if(item.Value.speakerList.Equals(ss))
                {
                    ans = true;
                    break;
                }
            }

            return ans;
        }

        /// <summary>
        /// 置換リストの追加処理
        /// </summary>
        private void CreateNewDefinitionList()
        {
            var ss = new ReplaceDefinitionList();
            Window wd = new EditReplaceList(ref ss);

            wd.ShowDialog();
            config.replaceDefinitionLists.Add(ss);
            ComboBoxRegexDefinitionLists.ItemsSource = null;
            ComboBoxRegexDefinitionLists.ItemsSource = config.replaceDefinitionLists;
            ComboBoxRegexDefinitionLists.SelectedItem = ss;
        }

        /// <summary>
        /// 置換リストの更新処理
        /// </summary>
        private void UpdateDefinitionList()
        {
            var ss = ComboBoxRegexDefinitionLists.SelectedItem as ReplaceDefinitionList;
            Window wd = new EditReplaceList(ref ss);

            wd.ShowDialog();
            ComboBoxRegexDefinitionLists.ItemsSource = null;
            ComboBoxRegexDefinitionLists.ItemsSource = config.replaceDefinitionLists;
            ComboBoxRegexDefinitionLists.SelectedItem = ss;
        }

        /// <summary>
        /// 置換リスト利用確認
        /// </summary>
        /// <param name="ss">利用確認したい置換リスト</param>
        /// <returns>利用中の時はtrue</returns>
        private bool IsUsedReplaceDefinitionList(ref ReplaceDefinitionList ss)
        {
            bool ans = false;

            foreach (var item in config.listenerConfigLists)
            {
                if (item.ReplaceListDefault.Equals(ss))
                {
                    ans = true;
                    break;
                }
                if (item.ReplaceListNoJapaneseJudge.Equals(ss))
                {
                    ans = true;
                    break;
                }
            }

            return ans;
        }

        ////// クリップボード対応用

        [DllImport("user32.dll")]
        private static extern bool AddClipboardFormatListener(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern bool RemoveClipboardFormatListener(IntPtr hWnd);

        // const int WM_DRAWCLIPBOARD = 0x0308;
        // const int WM_CHANGECBCHAIN = 0x030D;
        const int WM_CLIPBOARDUPDATE = 0x031D;

        /// <summary>
        /// クリップボード受信用ウインドウプロシジャ
        /// </summary>
        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_CLIPBOARDUPDATE)
            {
                try
                {
                    taskManager.ClipboardTask.AcceptData(Clipboard.GetText());
                }
                catch (Exception)
                {
                    //
                }
                handled = true;
            }

            return IntPtr.Zero;
        }

        private void SetClipboardListener()
        {
            AddClipboardFormatListener(WinHelper.Handle);
        }

        private void RemoveClipboardListener()
        {
            RemoveClipboardFormatListener(WinHelper.Handle);
        }

        // ログ

        public void Logging(string logtext)
        {
            Dispatcher.Invoke(() => {
                try
                {
                    TextBoxStatus.AppendText(logtext + Environment.NewLine);
                    TextBoxStatus.ScrollToEnd();
                }
                catch (Exception)
                {
                    TextBoxStatus.Clear();
                    TextBoxStatus.AppendText("-- Truncate log and create new log" + Environment.NewLine);
                }
            });
        }

        ///// イベントハンドラ


        private void ButtonNew_Click(object sender, RoutedEventArgs e)
        {
            CreateNewSpeakerList();
        }

        private void ButtonUpd_Click(object sender, RoutedEventArgs e)
        {
            if (ComboBoxSpeakerLists.SelectedIndex == -1) return;

            UpdateSpeakerList();
        }

        private void ButtonDel_Click(object sender, RoutedEventArgs e)
        {
            if (ComboBoxSpeakerLists.SelectedIndex == -1) return;

            var ss = ComboBoxSpeakerLists.SelectedItem as SpeakerFakeChanList;

            if(IsUsedSpeakerList(ref ss))
            {
                MessageBox.Show("指定の話者リストは使用されているので削除できません", "話者リストの削除確認");
                return;
            }

            if (MessageBoxResult.Yes == MessageBox.Show(String.Format(@"{0} を削除します", ss.Listname),"話者リストの削除確認",  MessageBoxButton.YesNo))
            {
                config.speakerLists.Remove(ss);
                ComboBoxSpeakerLists.Items.Refresh();
                ComboBoxSpeakerLists.SelectedIndex = 0;
            }
        }

        private void ButtonRegExNew_Click(object sender, RoutedEventArgs e)
        {
            CreateNewDefinitionList();
        }

        private void ButtonRegExUpd_Click(object sender, RoutedEventArgs e)
        {
            if (ComboBoxRegexDefinitionLists.SelectedIndex == -1) return;

            UpdateDefinitionList();
        }

        private void ButtonRegExDel_Click(object sender, RoutedEventArgs e)
        {
            if (ComboBoxRegexDefinitionLists.SelectedIndex == -1) return;

            var ss = ComboBoxRegexDefinitionLists.SelectedItem as ReplaceDefinitionList;

            if (IsUsedReplaceDefinitionList(ref ss))
            {
                MessageBox.Show("指定の置換リストは使用されているので削除できません", "置換リストの削除確認");
                return;
            }

            if (MessageBoxResult.Yes == MessageBox.Show(String.Format(@"{0} を削除します", ss.Listname), "置換リストの削除確認", MessageBoxButton.YesNo))
            {
                config.replaceDefinitionLists.Remove(ss);
                ComboBoxRegexDefinitionLists.Items.Refresh();
                ComboBoxRegexDefinitionLists.SelectedIndex = 0;
            }
        }

        private void ButtonSoloSpeechUpd_Click(object sender, RoutedEventArgs e)
        {
            Window wd = new EditSoloMessageList(ref config);
            wd.ShowDialog();
        }

        private void ButtonQueueUpd_Click(object sender, RoutedEventArgs e)
        {
            Window wd = new EditQueueControll(ref config.queueParam);
            wd.ShowDialog();
        }

        private void ButtonListenerUpd_Click(object sender, RoutedEventArgs e)
        {
            if (ComboBoxListenerConfigLists.SelectedIndex == -1) return;

            ListenerConfigBase Lsner = ComboBoxListenerConfigLists.SelectedItem as dynamic;

            Window wd = new EditListenerConfig(ref Lsner, ref config.speakerLists, ref config.replaceDefinitionLists);
            wd.ShowDialog();
            taskManager.TaskReBoot(ref Lsner);
        }

        private void TextBoxWinTitle_TextChanged(object sender, TextChangedEventArgs e)
        {
            config.fakeChan22WindowTitle = (sender as TextBox).Text;
            this.Title = config.fakeChan22WindowTitle + " " + config.versionStr;
        }

        private void ButtonCommentUpd_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new System.Windows.Forms.SaveFileDialog();

            dlg.InitialDirectory = config.commentXmGenlPath;
            dlg.Title = "comment.xml 生成フォルダ";
            dlg.Filter = "コメントxml|comment.xml";

            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                if (Directory.Exists(Directory.GetParent(dlg.FileName).FullName))
                {
                    config.commentXmGenlPath = Directory.GetParent(dlg.FileName).FullName;
                    TextBoxCommentXmlPath.Text = config.commentXmGenlPath;

                    taskManager.CommenGenTask.TaskStop();
                    taskManager.CommenGenTask.TaskStart(config.commentXmGenlPath);
                }
            }

        }

        private void ButtonResetListenerConfig_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("全リスナ設定を初期化します","初期化", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            {
                config.listenerConfigLists.Clear();

                MessageBox.Show("初期化しました。再度立ち上げてください。", "初期化");
                Application.Current.Shutdown();
            }
        }

        private void ButtonLogClear_Click(object sender, RoutedEventArgs e)
        {
            TextBoxStatus.Clear();
        }
    }
}