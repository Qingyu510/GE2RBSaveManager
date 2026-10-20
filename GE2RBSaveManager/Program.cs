using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using System.Drawing;
using System.Security.Cryptography;
using System.Diagnostics;
using System.Management;
using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace GE2RBSaveManager
{
    // 槽位文件夹名（各游戏 <AppId>/remote 下固定；见 SlotFolderNames / SlotFolderNamesGER）
    // 槽位1 = ...000，槽位2 = ...001，槽位3 = ...002
    // 转换核心与 GE2RBConvertTool 同源；AppId 438490 = GOD EATER 2 Rage Burst
    public class MainForm : Form
    {
        // ===== 转换核心常量（与 GE2RBConvertTool 一致）=====
        public static int SAVE_SIZE;
        public static int BODY_SIZE;
        public static int TRAILER_OFFSET;

        public const uint MASK32 = 0xFFFFFFFF;
        public const ulong MASK64 = 0xFFFFFFFFFFFFFFFF;
        public const ulong UNIVERSAL_CONSTANT = 0x8A51891CE32973E4;
        public const int CHKSUM_OFFSET = 0x10;
        public const int EZHASH_OFFSET = 0x14;
        public const int RTCHASH_OFFSET = 0x40;

        // 转换时强制清零的存档字段（明文偏移）
        // 亚洲版 = 原 PS 端（0x80050）；以下为亚洲版字段偏移
        public const int ASIA_MINVER_OFFSET = 0x7C7C4;  // 最低版本限制: 0=1.0, 1=1.1, 2=1.2 …
        public const int ASIA_RSTICK_OFFSET = 0x529D8;  // 右摇杆操作设置
        // 欧美版 = PS 美版（0x90050，与 PC 同大小）；以下为欧美版字段偏移
        public const int US_MINVER_OFFSET = 0x87EE4;    // 最低版本限制
        public const int US_RSTICK_OFFSET = 0x58578;    // 右摇杆操作设置

        // 偏移检测标记位（解密后明文，单字节 == 0x01）
        const int MARKER_US     = 0x86AAA;  // 单字节 == 0x01 -> 欧美版(PS美版)
        const int MARKER_STEAM  = 0x89C2A;  // 单字节 == 0x01 -> Steam/PC
        const int MARKER_ASIA   = 0x7B38A;  // 单字节 == 0x01 -> 亚洲版(原PS端)

        // 玩家名字/代号（解密后明文，UTF-8，0x00 结尾）
        const int PLAYER_NAME_OFFSET = 0x57;  // 名字
        const int ASIA_CODE_OFFSET   = 0x77;  // 代号（亚洲版）
        const int US_CODE_OFFSET     = 0x97;  // 代号（欧美版/Steam版）

        // 补丁文件（与 exe 同目录，便于热更新）
        static readonly string PatchAsiaMain = "ps_asia_steam_main.txt";
        static readonly string PatchAsiaCard = "ps_asia_steam_card.txt";
        static readonly string PatchUsMain   = "ps_us_steam_main.txt";
        static readonly string PatchUsCard   = "ps_us_steam_card.txt";

        // 平台枚举：Asia=原PS端(0x80050) / Us=欧美版(PS美版,0x90050) / Steam=PC(0x90050)
        public enum Plat { Unknown, Asia, Us, Steam }

        // Steam 账户项：Dir=userdata 下的目录名(SteamID)，Label=列表显示文本
        sealed class SteamAccount
        {
            public string Dir = "";
            public string Label = "";
            public override string ToString() => Label;
        }

        // ===== 游戏选择（顶部按钮）=====
        // AppId 决定槽位 / 替换 / 删除 / 备份 / 修复vdf 使用的 userdata 子目录
        // 438490 = 噬神者2：狂怒解放(GE2RB)；460870 = 噬神者：解放重生(GER)
        const int AppIdGE2RB = 438490;
        const int AppIdGER = 460870;
        static int AppId = AppIdGE2RB;   // 当前选中游戏（默认狂怒解放）

        // 手动选择 Steam 目录：false = 注册表自动检测优先，检测不到再弹文件夹选择窗口
        const bool ForcePickSteamFolder = false;
        // GE2RB(438490) 槽位文件夹名；末位 0/1/2 对应槽位 1/2/3
        static readonly string[] SlotFolderNames = {
            "GE211aa685531120001000",
            "GE211aa685531120001001",
            "GE211aa685531120001002",
        };
        // GER(460870) 槽位文件夹名；末位 0/1/2 对应槽位 1/2/3
        static readonly string[] SlotFolderNamesGER = {
            "GE10db0505531180001000",
            "GE10db0505531180001001",
            "GE10db0505531180001002",
        };
        // 当前游戏对应的槽位文件夹名
        static string[] SlotNames => AppId == AppIdGER ? SlotFolderNamesGER : SlotFolderNames;

        // ===== GUI 状态 =====
        string? draggedFolder = null;
        string? selectedAccount = null;
        string? steamRoot = null;
        string? userdataPath = null;

        // 备份目录：放在 GE2RBSaveManager.exe 旁边（程序同目录下的 backups），而非 steam userdata 内
        static readonly string ExeDir = Path.GetDirectoryName(Application.ExecutablePath) ?? ".";
        static readonly string BackupRoot = Path.Combine(ExeDir, "backups");

        Button[] btnSlot = new Button[3];
        Button[] btnBackup = new Button[3];
        Button[] btnDelete = new Button[3];
        Button[] btnReplace = new Button[3];
        string[] slotDirs = new string[3];

        // 控件
        Panel dropPanel = null!;
        Label lblDrop = null!;
        Button btnConv1 = null!;   // 转换目标按钮1（动态指向源平台之外的两个平台之一）
        Button btnConv2 = null!;   // 转换目标按钮2
        TextBox txtInfo = null!;
        ListBox lstUsers = null!;
        TextBox txtLog = null!;
        Button btnFixVdf = null!;
        Button btnOpenBackup = null!;
        Button btnGameGer = null!;      // 游戏选择：噬神者：解放重生
        Button btnGameGe2rb = null!;    // 游戏选择：噬神者2：狂怒解放
        Button btnDecrypt = null!;      // 解密（调试用）
        Button btnEncrypt = null!;      // 加密（调试用）

        public MainForm()
        {
            BuildLayout();
            LoadUsers();
        }

        // ===================== 布局 =====================
        void BuildLayout()
        {
            Text = "GE2RB 存档管理器";
            Width = 920; Height = 580;
            MinimumSize = new System.Drawing.Size(820, 480);

            var tlp = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
            tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 320));
            tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
            tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Controls.Add(tlp);

            // ---- 左列：拖入窗口 + 转换 + 转换信息 ----
            var pLeft = new Panel { Dock = DockStyle.Fill };
            dropPanel = new Panel { Dock = DockStyle.Top, Height = 180, BorderStyle = BorderStyle.FixedSingle, AllowDrop = true, BackColor = SystemColors.ControlLight };
            lblDrop = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Text = "将存档文件夹拖入此处\r\n（需含 SECURE.BIN）" };
            dropPanel.Controls.Add(lblDrop);
            dropPanel.DragEnter += (s, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            dropPanel.DragDrop += (s, e) =>
            {
                var f = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (f != null && f.Length > 0) OnDropFolder(f[0]);
            };
            btnConv1 = new Button { Dock = DockStyle.Fill, Text = "转换", Enabled = false };
            btnConv1.Click += OnConvButtonClick;
            btnConv2 = new Button { Dock = DockStyle.Fill, Text = "转换", Enabled = false };
            btnConv2.Click += OnConvButtonClick;
            btnDecrypt = new Button { Dock = DockStyle.Fill, Text = "解密" };
            btnDecrypt.Click += (s, e) => OnDecrypt();
            btnEncrypt = new Button { Dock = DockStyle.Fill, Text = "加密" };
            btnEncrypt.Click += (s, e) => OnEncrypt();

            // 转换：2 个目标按钮并排 1 排；解密/加密并排 1 排
            var tlpConvert = new TableLayoutPanel { Dock = DockStyle.Top, Height = 76, ColumnCount = 1, RowCount = 2 };
            tlpConvert.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            tlpConvert.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            var tlpConvTargets = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
            tlpConvTargets.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            tlpConvTargets.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            tlpConvTargets.Controls.Add(btnConv1, 0, 0);
            tlpConvTargets.Controls.Add(btnConv2, 1, 0);
            var tlpCrypto = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
            tlpCrypto.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            tlpCrypto.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            tlpCrypto.Controls.Add(btnDecrypt, 0, 0);
            tlpCrypto.Controls.Add(btnEncrypt, 1, 0);
            tlpConvert.Controls.Add(tlpConvTargets, 0, 0);
            tlpConvert.Controls.Add(tlpCrypto, 0, 1);

            // 游戏选择：拖入窗口上方，左右并排（左：解放重生 / 右：狂怒解放）
            var tlpGame = new TableLayoutPanel { Dock = DockStyle.Top, Height = 38, ColumnCount = 2, RowCount = 1 };
            tlpGame.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            tlpGame.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            btnGameGer = new Button { Dock = DockStyle.Fill, Text = "噬神者：解放重生", FlatStyle = FlatStyle.Flat };
            btnGameGe2rb = new Button { Dock = DockStyle.Fill, Text = "噬神者2：狂怒解放", FlatStyle = FlatStyle.Flat };
            btnGameGer.Click += (s, e) => SelectGame(AppIdGER);
            btnGameGe2rb.Click += (s, e) => SelectGame(AppIdGE2RB);
            tlpGame.Controls.Add(btnGameGer, 0, 0);
            tlpGame.Controls.Add(btnGameGe2rb, 1, 0);

            var lblInfo = new Label { Dock = DockStyle.Top, Height = 22, Text = "转换信息" };
            txtInfo = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
            pLeft.Controls.Add(txtInfo);
            pLeft.Controls.Add(lblInfo);
            pLeft.Controls.Add(tlpConvert);
            pLeft.Controls.Add(dropPanel);
            pLeft.Controls.Add(tlpGame);        // 最后 Add -> 排在最上方（拖入窗口之上）
            tlp.Controls.Add(pLeft, 0, 0);

            UpdateGameButtons();

            // ---- 中列：steam 用户列表 ----
            var pMid = new Panel { Dock = DockStyle.Fill };
            var lblUsers = new Label { Dock = DockStyle.Top, Height = 22, Text = "steam用户列表" };
            lstUsers = new ListBox { Dock = DockStyle.Fill };
            lstUsers.SelectedIndexChanged += (s, e) => OnSelectUser();
            // 点击用户列表（路径无效/列表为空时）也触发兜底重选
            lstUsers.Click += (s, e) => EnsureSteamPath();
            pMid.Controls.Add(lstUsers);
            pMid.Controls.Add(lblUsers);
            tlp.Controls.Add(pMid, 1, 0);

            // ---- 右列：槽位 + 修复vdf + 操作提示 + 打开备份 ----
            var pRight = new Panel { Dock = DockStyle.Fill };
            var slotsTlp = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 4, RowCount = 3, Height = 3 * 36 + 4 };
            slotsTlp.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
            slotsTlp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
            slotsTlp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
            slotsTlp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
            slotsTlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            slotsTlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            slotsTlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            for (int i = 0; i < 3; i++)
            {
                int idx = i;
                btnSlot[idx] = new Button { Text = $"槽位{idx + 1}：?", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
                btnBackup[idx] = new Button { Text = "备份", Dock = DockStyle.Fill };
                btnDelete[idx] = new Button { Text = "删除", Dock = DockStyle.Fill };
                btnReplace[idx] = new Button { Text = "替换", Dock = DockStyle.Fill };
                btnBackup[idx].Click += (s, e) => OnBackup(idx);
                btnDelete[idx].Click += (s, e) => OnDelete(idx);
                btnReplace[idx].Click += (s, e) => OnReplace(idx);
                slotsTlp.Controls.Add(btnSlot[idx], 0, idx);
                slotsTlp.Controls.Add(btnBackup[idx], 1, idx);
                slotsTlp.Controls.Add(btnDelete[idx], 2, idx);
                slotsTlp.Controls.Add(btnReplace[idx], 3, idx);
            }
            btnFixVdf = new Button { Dock = DockStyle.Top, Height = 36, Text = "修复vdf" };
            btnFixVdf.Click += (s, e) => OnFixVdf();
            var lblLog = new Label { Dock = DockStyle.Top, Height = 22, Text = "操作提示" };
            btnOpenBackup = new Button { Dock = DockStyle.Bottom, Height = 36, Text = "打开备份" };
            btnOpenBackup.Click += (s, e) => OnOpenBackup();
            txtLog = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
            pRight.Controls.Add(txtLog);
            pRight.Controls.Add(lblLog);
            pRight.Controls.Add(btnOpenBackup);
            pRight.Controls.Add(btnFixVdf);
            pRight.Controls.Add(slotsTlp);
            tlp.Controls.Add(pRight, 2, 0);
        }

        // ===================== 事件 =====================

        // 切换当前游戏：更新 AppId，刷新游戏按钮高亮与槽位显示
        void SelectGame(int appId)
        {
            if (AppId == appId) return;
            AppId = appId;
            UpdateGameButtons();
            if (selectedAccount != null) LoadSlots(selectedAccount);
            Log($"已切换游戏：{GameName(appId)}（AppId {appId}）");
        }

        // 当前选中的游戏按钮高亮，另一个暗下去（两者仍可点击，以便来回切换）
        void UpdateGameButtons()
        {
            bool ger = AppId == AppIdGER;
            btnGameGer.BackColor = ger ? SystemColors.ControlLightLight : SystemColors.ControlDark;
            btnGameGe2rb.BackColor = ger ? SystemColors.ControlDark : SystemColors.ControlLightLight;
        }

        static string GameName(int appId) =>
            appId == AppIdGER ? "噬神者：解放重生" : appId == AppIdGE2RB ? "噬神者2：狂怒解放" : appId.ToString();

        void OnDropFolder(string path)
        {
            if (File.Exists(path) && !Directory.Exists(path)) path = Path.GetDirectoryName(path) ?? path;
            if (!Directory.Exists(path)) { Log("拖入的不是有效文件夹。"); return; }
            draggedFolder = path;
            lblDrop.Text = path;
            string sec = Path.Combine(path, "SECURE.BIN");
            if (File.Exists(sec))
            {
                byte[] plain = DecryptSecure(sec);
                Plat src = plain.Length > 0 ? DetectOffset(plain) : Plat.Unknown;
                string name = "", codeName = "";
                if (src != Plat.Unknown)
                {
                    name = ReadUtf8Z(plain, PLAYER_NAME_OFFSET);
                    codeName = ReadUtf8Z(plain, src == Plat.Asia ? ASIA_CODE_OFFSET : US_CODE_OFFSET);
                }
                string platText = src == Plat.Unknown ? "未知" : PlatShort(src);
                txtInfo.Text = $"存档路径：{path}\r\n平台：{platText}\r\n名字：{name}\r\n代号：{codeName}";
                UpdateConvertButtons(src);
            }
            else
            {
                txtInfo.Text = $"存档路径：{path}\r\n（文件夹内未找到 SECURE.BIN）";
                UpdateConvertButtons(Plat.Unknown);
            }
        }

        // 根据源平台，把两个转换目标按钮指向另外两个平台（三态只需 2 个按钮）
        // 文字用短名：转换为PS亚洲版 / 转换为PS欧美版 / 转换为Steam版
        void UpdateConvertButtons(Plat src)
        {
            Plat[] all = { Plat.Asia, Plat.Us, Plat.Steam };
            Plat[] targets = all.Where(p => p != src && p != Plat.Unknown).ToArray();
            Button[] btns = { btnConv1, btnConv2 };
            for (int i = 0; i < btns.Length; i++)
            {
                if (i < targets.Length)
                {
                    btns[i].Tag = targets[i];
                    btns[i].Enabled = true;
                    btns[i].Text = "转换为" + PlatShort(targets[i]);
                }
                else
                {
                    btns[i].Tag = null;
                    btns[i].Enabled = false;
                    btns[i].Text = "转换";
                }
            }
        }

        void OnConvButtonClick(object sender, EventArgs e)
        {
            if (sender is Button btn && btn.Tag is Plat tgt)
                OnConvertTo(tgt);
        }

        void OnConvertTo(Plat target)
        {
            if (draggedFolder == null) { Log("请先拖入存档文件夹。"); return; }
            string sec = Path.Combine(draggedFolder, "SECURE.BIN");
            if (!File.Exists(sec)) { Log("拖入的文件夹内未找到 SECURE.BIN。"); return; }
            try
            {
                Plat src = DetectSourcePlatform(sec);
                if (src == Plat.Unknown) { Log("无法确定源平台（解密后未在标记位发现 0x01）。"); return; }
                if (src == target) { Log($"存档已是{PlatName(target)}，无需转换。"); return; }
                string parent = Path.GetDirectoryName(draggedFolder) ?? ".";
                string baseName = Path.GetFileName(draggedFolder.TrimEnd('\\', '/'));
                string tgtName = target == Plat.Asia ? "亚洲版" : target == Plat.Us ? "欧美版" : "Steam版";
                string outFolder = Path.Combine(parent, $"{baseName}_converted_{tgtName}");
                ConvertFolderTo(draggedFolder, outFolder, target);
                long outSize = new FileInfo(Path.Combine(outFolder, "SECURE.BIN")).Length;
                txtInfo.AppendText($"\r\n--- 转换完成 ---\r\n{PlatName(src)} → {PlatName(target)}\r\n输出：{outFolder}\r\n大小：{outSize} 字节 (0x{outSize:X})");
                Log($"转换完成：{PlatName(src)} → {PlatName(target)}（{outFolder}）");
            }
            catch (Exception ex) { Log("转换失败：" + ex.Message); }
        }

        // 仅解密：读 SECURE.BIN，用文末 rtchash 推导密钥，解密 body，输出 SECURE.BIN.dec（保留 trailer）
        void OnDecrypt()
        {
            if (draggedFolder == null) { Log("请先拖入存档文件夹。"); return; }
            string sec = Path.Combine(draggedFolder, "SECURE.BIN");
            if (!File.Exists(sec)) { Log("拖入的文件夹内未找到 SECURE.BIN（无可解密存档）。"); return; }
            try
            {
                byte[] ciphertext = File.ReadAllBytes(sec);
                if (ciphertext.Length <= 8) { Log("SECURE.BIN 过小，无法解密。"); return; }
                byte[] trailer = ciphertext.Skip(ciphertext.Length - 8).ToArray();
                ulong rtchash = BitConverter.ToUInt64(trailer, 0);
                var (key_lo, key_hi) = DeriveKey(rtchash);
                int bodyLen = ciphertext.Length - 8;
                byte[] plainBody = Decrypt(ciphertext.Take(bodyLen).ToArray(), key_lo, key_hi, bodyLen);
                byte[] plainFull = new byte[plainBody.Length + 8];
                Array.Copy(plainBody, plainFull, plainBody.Length);
                Array.Copy(trailer, 0, plainFull, plainBody.Length, 8);
                string outPath = Path.Combine(draggedFolder, "SECURE.BIN.dec");
                File.WriteAllBytes(outPath, plainFull);
                Plat plat = DetectOffset(plainFull);
                string platName = plat == Plat.Unknown ? "未知" : PlatName(plat);
                txtInfo.AppendText($"\r\n--- 解密完成 ---\r\n平台：{platName}\r\n明文：{outPath}\r\n大小：{plainFull.Length} 字节 (0x{plainFull.Length:X})");
                Log($"解密完成：{outPath}（仅解密 body，trailer 原样保留）");
            }
            catch (Exception ex) { Log("解密失败：" + ex.Message); }
        }

        // 仅加密：读 SECURE.BIN.dec，重算校验和，用其 rtchash 推导密钥加密 body，输出 SECURE.BIN.enc（保留 trailer）
        void OnEncrypt()
        {
            if (draggedFolder == null) { Log("请先拖入存档文件夹。"); return; }
            string decPath = Path.Combine(draggedFolder, "SECURE.BIN.dec");
            if (!File.Exists(decPath)) { Log("未找到 SECURE.BIN.dec（请先解密生成明文）。"); return; }
            try
            {
                byte[] plainFull = File.ReadAllBytes(decPath);
                if (plainFull.Length <= 8) { Log("SECURE.BIN.dec 过小，无法加密。"); return; }
                byte[] trailer = plainFull.Skip(plainFull.Length - 8).ToArray();
                ulong rtchash = BitConverter.ToUInt64(trailer, 0);
                var (key_lo, key_hi) = DeriveKey(rtchash);
                // 设置静态大小字段以便重算校验和
                SAVE_SIZE = plainFull.Length;
                BODY_SIZE = SAVE_SIZE - 8;
                TRAILER_OFFSET = BODY_SIZE;
                byte[] fixedPlain = PatchChecksums(plainFull);
                int bodyLen = BODY_SIZE;
                byte[] encryptedBody = Encrypt(fixedPlain.Take(bodyLen).ToArray(), key_lo, key_hi, bodyLen);
                byte[] newCipher = new byte[encryptedBody.Length + 8];
                Array.Copy(encryptedBody, newCipher, encryptedBody.Length);
                Array.Copy(trailer, 0, newCipher, encryptedBody.Length, 8);
                string outPath = Path.Combine(draggedFolder, "SECURE.BIN.enc");
                File.WriteAllBytes(outPath, newCipher);
                txtInfo.AppendText($"\r\n--- 加密完成（已重算校验和）---\r\n密文：{outPath}\r\n大小：{newCipher.Length} 字节 (0x{newCipher.Length:X})");
                Log($"加密完成：{outPath}（重算 CHKSUM/EZHASH 校验和后再加密）");
            }
            catch (Exception ex) { Log("加密失败：" + ex.Message); }
        }

        // 兜底：确保已选定有效的 Steam 路径；无效（未选/取消/选错）时弹警示窗口并让用户重新选择
        bool EnsureSteamPath()
        {
            if (!string.IsNullOrEmpty(steamRoot) && !string.IsNullOrEmpty(userdataPath) && Directory.Exists(userdataPath))
                return true;
            var r = MessageBox.Show(
                "尚未选择有效的 Steam 安装目录（上次未选择、已取消或选错）。\n是否现在重新选择 Steam 路径？",
                "未选择 Steam 路径", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r != DialogResult.Yes) { Log("已取消重新选择 Steam 路径。"); return false; }
            Log("重新触发选择 Steam 路径……");
            LoadUsers();
            if (!string.IsNullOrEmpty(userdataPath) && Directory.Exists(userdataPath))
            {
                Log("Steam 路径已重新选择：" + userdataPath);
                return true;
            }
            Log("重新选择 Steam 路径未成功。");
            return false;
        }

        void OnSelectUser()
        {
            if (!EnsureSteamPath()) return;
            if (lstUsers.SelectedItem == null) return;
            selectedAccount = lstUsers.SelectedItem is SteamAccount sa ? sa.Dir : lstUsers.SelectedItem.ToString();
            LoadSlots(selectedAccount);
            Log($"已选择账户：{selectedAccount}");
        }

        // 解密槽位 SECURE.BIN，读取解密后明文的名字(0x57)与代号处的 UTF-8 值（以 0x00 结尾）
        // 代号偏移：解放重生(GER) = 0x77；狂怒解放(GE2RB) = 0x97（代号为空时只返回名字）
        static string? ReadPlayerName(string dir)
        {
            string sec = Path.Combine(dir, "SECURE.BIN");
            if (!File.Exists(sec)) return null;
            byte[] ciphertext = File.ReadAllBytes(sec);
            if (ciphertext.Length <= 8) return null;
            byte[] trailer = ciphertext.Skip(ciphertext.Length - 8).ToArray();
            ulong rtchash = BitConverter.ToUInt64(trailer, 0);
            var (key_lo, key_hi) = DeriveKey(rtchash);
            int bodyLen = ciphertext.Length - 8;
            byte[] plain = Decrypt(ciphertext.Take(bodyLen).ToArray(), key_lo, key_hi, bodyLen);

            string ReadUtf8(int off)
            {
                if (plain.Length <= off) return "";
                int max = Math.Min(plain.Length - off, 64);
                int len = 0;
                while (len < max && plain[off + len] != 0) len++;
                return len == 0 ? "" : Encoding.UTF8.GetString(plain, off, len);
            }

            string a = ReadUtf8(0x57);
            if (string.IsNullOrEmpty(a)) return null;
            string b = ReadUtf8(AppId == AppIdGER ? ASIA_CODE_OFFSET : US_CODE_OFFSET);
            return string.IsNullOrEmpty(b) ? a : $"{a}({b})";
        }

        void LoadSlots(string account)
        {
            string remote = Path.Combine(userdataPath ?? "", account, AppId.ToString(), "remote");
            for (int i = 0; i < 3; i++)
            {
                string dir = Path.Combine(remote, SlotNames[i]);
                slotDirs[i] = dir;
                bool has = File.Exists(Path.Combine(dir, "SECURE.BIN"));
                if (has)
                {
                    string? name = null;
                    try { name = ReadPlayerName(dir); } catch { name = null; }
                    btnSlot[i].Text = $"槽位{i + 1}：{(string.IsNullOrEmpty(name) ? "有" : name)}";
                }
                else
                {
                    btnSlot[i].Text = $"槽位{i + 1}：无";
                }
            }
        }

        void OnBackup(int i)
        {
            if (!EnsureSteamPath()) return;
            if (selectedAccount == null) { Log("请先选择 steam 账户。"); return; }
            if (string.IsNullOrEmpty(slotDirs[i]) || !Directory.Exists(slotDirs[i])) { Log($"槽位{i + 1} 不存在，无需备份。"); return; }
            string backupRoot = BackupRoot;
            Directory.CreateDirectory(backupRoot);
            string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string dest = Path.Combine(backupRoot, SlotNames[i] + "_" + ts);
            CopyDirectory(slotDirs[i], dest);
            Log($"已备份槽位{i + 1} → {dest}");
        }

        void OnDelete(int i)
        {
            if (!EnsureSteamPath()) return;
            if (selectedAccount == null) { Log("请先选择 steam 账户。"); return; }
            if (string.IsNullOrEmpty(slotDirs[i]) || !Directory.Exists(slotDirs[i])) { Log($"槽位{i + 1} 不存在。"); return; }
            var r = MessageBox.Show($"确定删除槽位{i + 1} 下的所有文件？此操作不可恢复！", "删除确认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r != DialogResult.Yes) { Log($"已取消删除槽位{i + 1}。"); return; }
            int n = 0;
            foreach (string f in Directory.GetFiles(slotDirs[i])) { File.Delete(f); n++; }
            foreach (string d in Directory.GetDirectories(slotDirs[i])) { Directory.Delete(d, true); n++; }
            Log($"已删除槽位{i + 1} 下的 {n} 项：{slotDirs[i]}");
            LoadSlots(selectedAccount);
        }

        void OnReplace(int i)
        {
            if (!EnsureSteamPath()) return;
            if (draggedFolder == null) { Log("请先拖入存档文件夹。"); return; }
            if (selectedAccount == null) { Log("请先选择 steam 账户。"); return; }
            string sec = Path.Combine(draggedFolder, "SECURE.BIN");
            if (!File.Exists(sec)) { Log("拖入的文件夹内未找到 SECURE.BIN。"); return; }
            Plat src = DetectSourcePlatform(sec);
            if (src == Plat.Unknown) { Log("无法确定拖入存档平台，无法替换。"); return; }

            string? sourceForReplace = null;
            string? tempConv = null;
            if (src == Plat.Steam)
            {
                var r = MessageBox.Show($"是否替换槽位：{i + 1}？", "替换确认", MessageBoxButtons.YesNo);
                if (r != DialogResult.Yes) { Log($"已取消替换槽位{i + 1}。"); return; }
                sourceForReplace = draggedFolder;
            }
            else
            {
                var r = MessageBox.Show($"是否先转换为Steam存档(PC)后替换槽位：{i + 1}？\n当前：{PlatName(src)}", "替换确认", MessageBoxButtons.YesNo);
                if (r != DialogResult.Yes) { Log($"已取消替换槽位{i + 1}。"); return; }
                tempConv = Path.Combine(Path.GetTempPath(), "GE2RB_conv_" + Guid.NewGuid().ToString("N"));
                try { ConvertFolderTo(draggedFolder, tempConv, Plat.Steam); }
                catch (Exception ex) { Log("转换失败：" + ex.Message); return; }
                sourceForReplace = tempConv;
            }
            try
            {
                Directory.CreateDirectory(slotDirs[i]);
                CopyDirectory(sourceForReplace, slotDirs[i]);
                Log($"已替换槽位{i + 1}（来源：{PlatName(src)}）：{slotDirs[i]}");
                LoadSlots(selectedAccount);
                var rFix = MessageBox.Show("是否修复vdf？\n修复steam云存档状态，以此解决卡片加载错误问题。", "修复vdf", MessageBoxButtons.YesNo);
                if (rFix == DialogResult.Yes) RepairVdf();
            }
            catch (Exception ex) { Log("替换失败：" + ex.Message); }
            finally { if (tempConv != null) { try { Directory.Delete(tempConv, true); } catch { } } }
        }

        void OnFixVdf()
        {
            RepairVdf();
        }

        void RepairVdf()
        {
            if (selectedAccount == null) { Log("请先选择 steam 账户。"); return; }
            string remote = Path.Combine(userdataPath ?? "", selectedAccount, AppId.ToString(), "remote");
            if (!Directory.Exists(remote)) { Log($"未找到 remote 目录：{remote}"); return; }

            Log("修复vdf文件具体效果：\r\n修复steam云存档状态，以此解决卡片加载错误问题。");

            if (IsSteamRunning())
            {
                var r = MessageBox.Show("修复vdf文件具体效果：\n修复steam云存档状态，以此解决卡片加载错误问题。\n修复steam存档加载需要先关闭steam，是否关闭steam并执行修复？", "修复vdf", MessageBoxButtons.YesNo);
                if (r != DialogResult.Yes) { Log("已取消修复vdf（steam 未关闭）。"); return; }
                KillSteam();
                System.Threading.Thread.Sleep(800);
            }
            else
            {
                // steam 未打开：弹出效果提示，随后直接执行修复
                MessageBox.Show("修复vdf文件具体效果：\n修复steam云存档状态，以此解决卡片加载错误问题。", "修复vdf", MessageBoxButtons.OK);
            }
            try
            {
                GenerateRemoteCacheVdf(remote);
                Log($"修复vdf完成：{Path.Combine(remote, "remotecache.vdf")}");
            }
            catch (Exception ex) { Log("修复vdf失败：" + ex.Message); }
        }

        void OnOpenBackup()
        {
            if (selectedAccount == null) { Log("请先选择 steam 账户。"); return; }
            string backupRoot = BackupRoot;
            Directory.CreateDirectory(backupRoot);
            try { Process.Start("explorer.exe", backupRoot); }
            catch (Exception ex) { Log("打开备份目录失败：" + ex.Message); }
        }

        void Log(string msg)
        {
            string ts = DateTime.Now.ToString("HH:mm:ss");
            txtLog.AppendText($"[{ts}] {msg}\r\n");
        }

        // ===================== Steam / 用户 =====================
        void LoadUsers()
        {
            steamRoot = ForcePickSteamFolder ? null : FindSteamPath();
            // 注册表拿不到（或拿到的目录里没有 userdata）：弹出文件夹选择窗口，让用户自己指定 Steam 路径
            if (steamRoot == null || !Directory.Exists(Path.Combine(steamRoot, "userdata")))
            {
                Log(steamRoot == null
                    ? "未从注册表找到 Steam 安装路径，请手动选择 Steam 目录。"
                    : "注册表指向的目录下没有 userdata：" + steamRoot);
                string? picked = PromptSteamFolder(steamRoot);
                if (string.IsNullOrEmpty(picked))
                {
                    Log("已取消选择 Steam 目录。可点击「备份 / 删除 / 替换 / steam 用户列表」重新触发选择 Steam 路径。");
                    return;
                }
                if (!Directory.Exists(Path.Combine(picked, "userdata")))
                {
                    Log("所选目录下没有 userdata 文件夹，可能不是 Steam 安装目录：" + picked
                        + "。可点击「备份 / 删除 / 替换 / steam 用户列表」重新触发选择 Steam 路径。");
                    return;
                }
                steamRoot = picked;
            }
            userdataPath = Path.Combine(steamRoot, "userdata");
            if (!Directory.Exists(userdataPath)) { Log("未找到 userdata 目录：" + userdataPath + "。可点击「备份 / 删除 / 替换 / steam 用户列表」重新触发选择 Steam 路径。"); return; }
            // 读取 config/loginusers.vdf，拿到 SteamID -> 昵称/登录账户名 的映射
            var nameMap = ParseLoginUsers(Path.Combine(steamRoot, "config", "loginusers.vdf"));
            var accounts = Directory.GetDirectories(userdataPath)
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrEmpty(n))
                .ToList();
            lstUsers.Items.Clear();
            foreach (var a in accounts)
            {
                string dir = a!;
                string label = dir;
                if (nameMap.TryGetValue(dir, out var nm))
                {
                    string persona = nm.persona, account = nm.account;
                    if (!string.IsNullOrEmpty(persona) && !string.IsNullOrEmpty(account) && account != persona)
                        label = persona + "（" + account + "）";
                    else if (!string.IsNullOrEmpty(persona)) label = persona;
                    else if (!string.IsNullOrEmpty(account)) label = account;
                }
                lstUsers.Items.Add(new SteamAccount { Dir = dir, Label = label });
            }
            Log($"已加载 {accounts.Count} 个 steam 账户（userdata）。");
        }

        // 解析 Steam 的 config/loginusers.vdf，返回 SteamID(目录名) -> (昵称, 登录账户名)
        static Dictionary<string, (string persona, string account)> ParseLoginUsers(string vdfPath)
        {
            var map = new Dictionary<string, (string, string)>();
            // SteamID64 与 userdata 目录名(32 位 accountID) 都登记，两种目录命名都能命中
            void Store(string id, string per, string acc)
            {
                map[id] = (per, acc);
                if (long.TryParse(id, out long sid) && sid > 76561197960265728L)
                    map[(sid - 76561197960265728L).ToString()] = (per, acc);
            }
            if (!File.Exists(vdfPath)) return map;
            string text;
            try { text = File.ReadAllText(vdfPath); } catch { return map; }
            var tokens = System.Text.RegularExpressions.Regex.Matches(text, "\"([^\"]*)\"")
                .Cast<System.Text.RegularExpressions.Match>()
                .Select(m => m.Groups[1].Value)
                .ToList();
            string? curId = null;
            string persona = "", account = "";
            for (int i = 0; i < tokens.Count; i++)
            {
                string t = tokens[i];
                if (t == "users") continue;
                bool isId = t.Length >= 15 && t.All(char.IsDigit);
                if (isId)
                {
                    if (curId != null) Store(curId, persona, account);
                    curId = t; persona = ""; account = "";
                    continue;
                }
                if (curId == null) continue;
                string val = i + 1 < tokens.Count ? tokens[i + 1] : "";
                if (t == "PersonaName") persona = val;
                else if (t == "AccountName") account = val;
            }
            if (curId != null) Store(curId, persona, account);
            return map;
        }

        // 弹出资源管理器风格的文件夹选择窗口（Vista+ IFileOpenDialog + FOS_PICKFOLDERS）
        // 外观与「打开文件」对话框一致：带地址栏，可直接输入/粘贴路径
        static string? PromptSteamFolder(string? initial)
        {
            return ShowFolderPicker("选择 Steam 安装目录（需包含 userdata 文件夹）", initial);
        }

        const uint FOS_PICKFOLDERS = 0x00000020;
        const uint FOS_FORCEFILESYSTEM = 0x00000040;
        const uint FOS_PATHMUSTEXIST = 0x00000800;
        const uint SIGDN_FILESYSPATH = 0x80058000;

        static string? ShowFolderPicker(string title, string? initial)
        {
            var dialog = (IFileDialog)new FileOpenDialogRCW();
            try
            {
                dialog.GetOptions(out uint opts);
                dialog.SetOptions(opts | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST);
                if (!string.IsNullOrEmpty(title)) dialog.SetTitle(title);

                if (!string.IsNullOrEmpty(initial) && Directory.Exists(initial))
                {
                    Guid iid = typeof(IShellItem).GUID;
                    SHCreateItemFromParsingName(initial!, IntPtr.Zero, ref iid, out IShellItem folder);
                    dialog.SetFolder(folder);
                    Marshal.ReleaseComObject(folder);
                }

                int hr = dialog.Show(IntPtr.Zero);
                if (hr != 0) return null;   // 用户取消

                dialog.GetResult(out IShellItem item);
                item.GetDisplayName(SIGDN_FILESYSPATH, out string path);
                Marshal.ReleaseComObject(item);
                return path;
            }
            finally
            {
                Marshal.ReleaseComObject(dialog);
            }
        }

        [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IFileDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
            void SetFileTypeIndex(uint iFileType);
            void GetFileTypeIndex(out uint piFileType);
            void Advise(IntPtr pfde, out uint pdwCookie);
            void Unadvise(uint dwCookie);
            void SetOptions(uint fos);
            void GetOptions(out uint pfos);
            void SetDefaultFolder(IShellItem psi);
            void SetFolder(IShellItem psi);
            void GetFolder(out IShellItem ppsi);
            void GetCurrentSelection(out IShellItem ppsi);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
            void GetResult(out IShellItem ppsi);
            void AddPlace(IShellItem psi, int fdap);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
            void Close(int hr);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr pFilter);
        }

        [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem ppsi);
            void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
            void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            void Compare(IShellItem psi, uint hint, out int piOrder);
        }

        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        class FileOpenDialogRCW { }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        static extern void SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
            IntPtr pbc, ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

        static string? FindSteamPath()
        {
            // 注册表里 Steam 的安装路径；不同 hive/视图下值名可能是 InstallPath / SteamPath / SteamExe
            string? FromRegistry(RegistryHive hive, RegistryView view)
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var key = baseKey.OpenSubKey(@"Software\Valve\Steam");
                    if (key != null)
                    {
                        foreach (var vn in new[] { "InstallPath", "SteamPath", "SteamExe" })
                        {
                            var v = key.GetValue(vn);
                            if (v == null) continue;
                            string p = v.ToString().Replace('/', '\\');
                            if (vn == "SteamExe") p = Path.GetDirectoryName(p) ?? p;
                            if (Directory.Exists(p)) return p;
                        }
                    }
                }
                catch { }
                return null;
            }
            return FromRegistry(RegistryHive.LocalMachine, RegistryView.Registry32)
                ?? FromRegistry(RegistryHive.LocalMachine, RegistryView.Registry64)
                ?? FromRegistry(RegistryHive.CurrentUser, RegistryView.Default);
        }

        static bool IsSteamRunning()
        {
            return Process.GetProcessesByName("steam").Length > 0
                || Process.GetProcessesByName("steamwebhelper").Length > 0;
        }

        static void KillSteam()
        {
            foreach (var p in Process.GetProcessesByName("steam")) { try { p.Kill(); } catch { } }
            foreach (var p in Process.GetProcessesByName("steamwebhelper")) { try { p.Kill(); } catch { } }
        }

        // ===================== 平台判定（解密后偏移检测）=====================

        // 解密后明文偏移检测（单字节 == 0x01）：0x86AAA=欧美 / 0x89C2A=PC / 0x7B38A=亚洲(原PS端)
        // 亚洲版(0x80050) 文件读不到 0x86AAA/0x89C2A（超出长度），只会命中 0x7B38A
        static Plat DetectOffset(byte[] d)
        {
            static bool B(byte[] data, int off) => data.Length > off && data[off] == 0x01;
            if (B(d, MARKER_US)) return Plat.Us;
            if (B(d, MARKER_STEAM)) return Plat.Steam;
            if (B(d, MARKER_ASIA)) return Plat.Asia;
            return Plat.Unknown;
        }

        // 解密 SECURE.BIN：body 用文末 rtchash 推导密钥解密，返回明文 body
        static byte[] DecryptSecure(string securePath)
        {
            byte[] ciphertext = File.ReadAllBytes(securePath);
            if (ciphertext.Length <= 8) return Array.Empty<byte>();
            byte[] trailer = ciphertext.Skip(ciphertext.Length - 8).ToArray();
            ulong rtchash = BitConverter.ToUInt64(trailer, 0);
            var (key_lo, key_hi) = DeriveKey(rtchash);
            int bodyLen = ciphertext.Length - 8;
            return Decrypt(ciphertext.Take(bodyLen).ToArray(), key_lo, key_hi, bodyLen);
        }

        static Plat DetectSourcePlatform(string securePath)
        {
            byte[] plain = DecryptSecure(securePath);
            return plain.Length == 0 ? Plat.Unknown : DetectOffset(plain);
        }

        // 读取明文偏移处的 UTF-8 字符串（以 0x00 结尾）
        static string ReadUtf8Z(byte[] d, int off)
        {
            if (d == null || off < 0 || off >= d.Length) return "";
            int end = off;
            while (end < d.Length && d[end] != 0) end++;
            return Encoding.UTF8.GetString(d, off, end - off);
        }

        static string PlatName(Plat p) =>
            p == Plat.Asia ? "亚洲版(原PS端)" : p == Plat.Us ? "欧美版(PS美版)" : p == Plat.Steam ? "Steam版(PC)" : "未知";

        // 转换按钮短名
        static string PlatShort(Plat p) =>
            p == Plat.Asia ? "PS亚洲版" : p == Plat.Us ? "PS欧美版" : p == Plat.Steam ? "Steam版" : "?";

        // ===================== PS↔PC 转换核心 =====================
        static ulong Mul64(uint lo, uint hi, uint m_lo, uint m_hi)
        {
            if (m_hi == 0 && hi == 0)
                return (ulong)lo * m_lo & MASK64;
            ulong a = ((ulong)hi << 32) | lo;
            ulong b = ((ulong)m_hi << 32) | m_lo;
            return (a * b) & MASK64;
        }

        static ulong LcgStep(ulong state)
        {
            uint lo = (uint)(state & MASK32);
            uint hi = (uint)((state >> 32) & MASK32);
            ulong uVar6 = Mul64(lo, hi, 0x01000001, 0);
            uint u6_lo = (uint)(uVar6 & MASK32);
            uint u6_hi = (uint)((uVar6 >> 32) & MASK32);
            uint part_low = (u6_lo * 0x100) & MASK32;
            uint part_high = (((u6_hi << 8) & MASK32) | (u6_lo >> 0x18)) & MASK32;
            ulong concat = ((ulong)part_high << 32) | part_low;
            return ((uVar6 >> 0x10) + concat) & MASK64;
        }

        static float F01F32(uint bits32)
        {
            uint bits = (bits32 & 0x7fffff) | 0x3f800000;
            byte[] bytes = BitConverter.GetBytes(bits);
            if (!BitConverter.IsLittleEndian) Array.Reverse(bytes);
            float val = BitConverter.ToSingle(bytes, 0);
            return val - 1.0f;
        }

        static void GenTable(ulong state, out int[] fwd, out int[] inv, out ulong outState)
        {
            fwd = new int[256];
            inv = new int[256];
            List<int> local = Enumerable.Range(0, 256).ToList();
            int remaining = 256;
            int idx = 0;
            for (int i = 0; i < 256; i++)
            {
                state = LcgStep(state);
                float f = F01F32((uint)(state & MASK32));
                float product = f * remaining;
                int pick = (int)product;
                if (pick >= remaining) pick = remaining - 1;
                if (pick < 0) pick = 0;
                int val = local[pick];
                local[pick] = local[remaining - 1];
                fwd[idx] = val;
                inv[val] = idx;
                idx++;
                remaining--;
            }
            outState = state;
        }

        static void InitState(uint key_lo, uint key_hi, out uint running, out ulong seed)
        {
            ulong key = ((ulong)key_hi << 32) | key_lo;
            ulong lVar1 = LcgStep(key);
            ulong seedLocal = LcgStep(lVar1);
            float f_hi = F01F32((uint)(lVar1 & MASK32));
            float f_lo = F01F32((uint)(seedLocal & MASK32));
            uint part_hi = (uint)((int)(f_hi * 65536.0f) & 0xFFFF);
            uint part_lo = (uint)((int)(f_lo * 65536.0f) & 0xFFFF);
            running = ((part_hi << 16) | part_lo) & MASK32;
            seed = seedLocal;
        }

        static (uint, uint) DeriveKey(ulong rtchash)
        {
            ulong combined = (UNIVERSAL_CONSTANT + rtchash) & MASK64;
            return ((uint)(combined & MASK32), (uint)((combined >> 32) & MASK32));
        }

        public static byte[] Decrypt(byte[] ciphertext, uint key_lo, uint key_hi, int length)
        {
            uint running; ulong seed;
            InitState(key_lo, key_hi, out running, out seed);
            byte[] dest = new byte[length];
            int pos = 0;
            while (pos < length)
            {
                int chunk_len = Math.Min(256, length - pos);
                int[] fwd; int[] inv; ulong newSeed;
                GenTable(seed, out fwd, out inv, out newSeed);
                seed = newSeed;
                int nwords = ((chunk_len - 1) >> 2) + 1;
                for (int w = 0; w < nwords; w++)
                {
                    int off = pos + w * 4;
                    uint src_word = BitConverter.ToUInt32(ciphertext, off);
                    running = (running ^ src_word) & MASK32;
                    byte[] stateBytes = BitConverter.GetBytes(running);
                    if (!BitConverter.IsLittleEndian) Array.Reverse(stateBytes);
                    Array.Copy(stateBytes, 0, dest, off, 4);
                }
                for (int i = 0; i < chunk_len; i++)
                {
                    int b = dest[pos + i];
                    dest[pos + i] = (byte)((inv[b] - i) & 0xFF);
                }
                pos += chunk_len;
            }
            return dest;
        }

        public static byte[] Encrypt(byte[] plaintext, uint key_lo, uint key_hi, int length)
        {
            uint running; ulong seed;
            InitState(key_lo, key_hi, out running, out seed);
            byte[] dest = new byte[length];
            int pos = 0;
            while (pos < length)
            {
                int chunk_len = Math.Min(256, length - pos);
                int[] fwd; int[] inv; ulong newSeed;
                GenTable(seed, out fwd, out inv, out newSeed);
                seed = newSeed;
                int nwords = ((chunk_len - 1) >> 2) + 1;
                byte[] step1 = new byte[nwords * 4];
                for (int i = 0; i < chunk_len; i++)
                {
                    byte p = plaintext[pos + i];
                    step1[i] = (byte)fwd[(p + i) & 0xFF];
                }
                for (int w = 0; w < nwords; w++)
                {
                    int off = w * 4;
                    uint new_state = BitConverter.ToUInt32(step1, off);
                    uint cipher_word = (running ^ new_state) & MASK32;
                    byte[] cipherBytes = BitConverter.GetBytes(cipher_word);
                    if (!BitConverter.IsLittleEndian) Array.Reverse(cipherBytes);
                    Array.Copy(cipherBytes, 0, dest, pos + off, 4);
                    running = new_state;
                }
                pos += chunk_len;
            }
            return dest;
        }

        public static (uint, uint) ComputeChecksums(byte[] data)
        {
            if (data.Length != SAVE_SIZE)
                throw new ArgumentException($"期望 {SAVE_SIZE} 字节，实际 {data.Length}");
            byte[] work = (byte[])data.Clone();
            int[] zeroOffsets = { CHKSUM_OFFSET, EZHASH_OFFSET, RTCHASH_OFFSET, RTCHASH_OFFSET + 4, TRAILER_OFFSET, TRAILER_OFFSET + 4 };
            foreach (int off in zeroOffsets)
                Array.Copy(new byte[4], 0, work, off, 4);
            uint chksum = 0, ezhash = 0;
            foreach (byte b in work)
            {
                chksum = (chksum + b) & MASK32;
                uint prod = (ezhash * 0xE0) & MASK32;
                ezhash = ((prod ^ b) + (prod >> 8)) & MASK32;
            }
            return (chksum, ezhash);
        }

        public static byte[] PatchChecksums(byte[] data)
        {
            var (chksum, ezhash) = ComputeChecksums(data);
            byte[] outData = (byte[])data.Clone();
            byte[] chkBytes = BitConverter.GetBytes(chksum);
            if (!BitConverter.IsLittleEndian) Array.Reverse(chkBytes);
            Array.Copy(chkBytes, 0, outData, CHKSUM_OFFSET, 4);
            byte[] ezBytes = BitConverter.GetBytes(ezhash);
            if (!BitConverter.IsLittleEndian) Array.Reverse(ezBytes);
            Array.Copy(ezBytes, 0, outData, EZHASH_OFFSET, 4);
            return outData;
        }

        static string[] ReadEmbeddedResource(string resourceName)
        {
            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
                throw new Exception($"嵌入式资源 {resourceName} 未找到。");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        }

        enum OpType { Delete, Insert }
        class Operation
        {
            public long Address;
            public OpType Type;
            public int Length;
            public byte FillValue = 0x00;
        }

        static List<Operation> LoadOperations(string[] lines)
        {
            var ops = new List<Operation>();
            foreach (string line in lines)
            {
                string trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#")) continue;
                string[] parts = trimmed.Split(',');
                if (parts.Length < 3) continue;
                try
                {
                    var op = new Operation();
                    string addrStr = parts[0].Trim();
                    op.Address = addrStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ?
                        Convert.ToInt64(addrStr, 16) : long.Parse(addrStr);
                    string type = parts[1].Trim().ToUpper();
                    if (type == "D") op.Type = OpType.Delete;
                    else if (type == "I") op.Type = OpType.Insert;
                    else throw new Exception($"未知类型: {type}");
                    string lenStr = parts[2].Trim();
                    op.Length = lenStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ?
                        Convert.ToInt32(lenStr, 16) : int.Parse(lenStr);
                    if (parts.Length >= 4 && !string.IsNullOrEmpty(parts[3]))
                    {
                        string valStr = parts[3].Trim();
                        op.FillValue = valStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ?
                            Convert.ToByte(valStr, 16) : byte.Parse(valStr);
                    }
                    ops.Add(op);
                }
                catch { }
            }
            return ops;
        }

        static byte[] ApplyPatch(byte[] data, string[] lines, bool reverse)
        {
            var operations = LoadOperations(lines);
            if (operations.Count == 0) throw new Exception("未找到有效操作");
            if (reverse)
                foreach (var op in operations)
                    op.Type = (op.Type == OpType.Delete) ? OpType.Insert : OpType.Delete;
            var sorted = reverse ?
                operations.OrderBy(o => o.Address).ToList() :
                operations.OrderByDescending(o => o.Address).ToList();
            using (var ms = new MemoryStream())
            {
                ms.Write(data, 0, data.Length);
                ms.Position = 0;
                foreach (var op in sorted)
                {
                    if (op.Address < 0 || op.Address > ms.Length) continue;
                    switch (op.Type)
                    {
                        case OpType.Delete:
                            int delLen = (int)Math.Min(op.Length, ms.Length - op.Address);
                            if (delLen <= 0) break;
                            byte[] buffer = new byte[ms.Length - op.Address - delLen];
                            ms.Position = op.Address + delLen;
                            ms.Read(buffer, 0, buffer.Length);
                            ms.Position = op.Address;
                            ms.Write(buffer, 0, buffer.Length);
                            ms.SetLength(ms.Length - delLen);
                            break;
                        case OpType.Insert:
                            byte[] insertData = Enumerable.Repeat(op.FillValue, op.Length).ToArray();
                            byte[] tail = new byte[ms.Length - op.Address];
                            ms.Position = op.Address;
                            ms.Read(tail, 0, tail.Length);
                            ms.Position = op.Address;
                            ms.Write(insertData, 0, insertData.Length);
                            ms.Write(tail, 0, tail.Length);
                            break;
                    }
                }
                return ms.ToArray();
            }
        }

        static void CopyDirectory(string sourceDir, string targetDir)
        {
            Directory.CreateDirectory(targetDir);
            foreach (string file in Directory.GetFiles(sourceDir))
                File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)), true);
            foreach (string subDir in Directory.GetDirectories(sourceDir))
                CopyDirectory(subDir, Path.Combine(targetDir, Path.GetFileName(subDir)));
        }

        // ===================== 转换（偏移检测驱动，替代原 size 检测 ConvertSaveFolder）=====================
        class ConvPlan
        {
            public string MainPatch = "";
            public string CardPatch = "";
            public bool Reverse;
            public bool IsUs;  // true=欧美版补丁(清 0x87EE4/0x58578)；false=亚洲版补丁(清 0x7C7C4/0x529D8)
        }

        static ConvPlan? GetPlan(Plat src, Plat target)
        {
            if ((src == Plat.Asia && target == Plat.Steam) || (src == Plat.Steam && target == Plat.Asia))
                return new ConvPlan { MainPatch = PatchAsiaMain, CardPatch = PatchAsiaCard, Reverse = (src == Plat.Asia), IsUs = false };
            if ((src == Plat.Us && target == Plat.Steam) || (src == Plat.Steam && target == Plat.Us))
                return new ConvPlan { MainPatch = PatchUsMain, CardPatch = PatchUsCard, Reverse = (src == Plat.Steam), IsUs = true };
            return null;  // 亚洲↔欧美：无直接补丁，由 ConvertFolderTo 经 Steam 中转
        }

        static string[] ReadPatchLines(string fileName)
        {
            // 补丁已嵌入 exe，资源名前缀与程序集/根命名空间一致
            return ReadEmbeddedResource("GE2RBSaveManager." + fileName);
        }

        static void ClearFields(byte[] d, int offMin, int offRstick)
        {
            if (d.Length > offMin) d[offMin] = 0;
            if (d.Length > offRstick) d[offRstick] = 0;
        }

        // 全自动：解密 -> 选补丁 -> 清字段 -> ApplyPatch -> 重算校验和 -> 加密 -> 输出
        // 欧美↔亚洲 无直接补丁，自动经 Steam 中转（先转 Steam 再转目标，按用户指示）
        public static void ConvertFolderTo(string inputFolder, string outputFolder, Plat target, Plat? srcHint = null)
        {
            string secIn = Path.Combine(inputFolder, "SECURE.BIN");
            if (!File.Exists(secIn)) throw new FileNotFoundException("未找到 SECURE.BIN", secIn);
            Plat src = srcHint ?? DetectSourcePlatform(secIn);
            if (src == Plat.Unknown) throw new Exception("无法确定源平台（解密后未在 0x86AAA/0x89C2A/0x7B38A 发现 0x01 标记）");
            if (src == target) { CopyDirectory(inputFolder, outputFolder); return; }

            // 亚洲↔欧美：经 Steam 中转
            if (src != Plat.Steam && target != Plat.Steam)
            {
                string temp = Path.Combine(Path.GetTempPath(), "GE2RB_chain_" + Guid.NewGuid().ToString("N"));
                try
                {
                    ConvertFolderTo(inputFolder, temp, Plat.Steam, srcHint: src);
                    ConvertFolderTo(temp, outputFolder, target, srcHint: Plat.Steam);
                }
                finally { try { Directory.Delete(temp, true); } catch { } }
                return;
            }

            ConvPlan? plan = GetPlan(src, target);
            if (plan == null) throw new Exception($"暂未提供 {PlatName(src)}↔{PlatName(target)} 的转换补丁");

            if (Directory.Exists(outputFolder)) Directory.Delete(outputFolder, true);
            Directory.CreateDirectory(outputFolder);
            CopyDirectory(inputFolder, outputFolder);

            byte[] ciphertext = File.ReadAllBytes(Path.Combine(outputFolder, "SECURE.BIN"));
            byte[] trailer = ciphertext.Skip(ciphertext.Length - 8).ToArray();
            ulong rtchash = BitConverter.ToUInt64(trailer, 0);
            var (key_lo, key_hi) = DeriveKey(rtchash);
            int body_len = ciphertext.Length - 8;
            byte[] plainBody = Decrypt(ciphertext.Take(body_len).ToArray(), key_lo, key_hi, body_len);
            byte[] plainFull = new byte[plainBody.Length + 8];
            Array.Copy(plainBody, plainFull, plainBody.Length);
            Array.Copy(trailer, 0, plainFull, plainBody.Length, 8);

            string[] mainLines = ReadPatchLines(plan.MainPatch);

            // 源是 PS 端（亚洲/欧美）时，结构修改前清零对应字段
            if (plan.IsUs && src == Plat.Us) ClearFields(plainFull, US_MINVER_OFFSET, US_RSTICK_OFFSET);
            if (!plan.IsUs && src == Plat.Asia) ClearFields(plainFull, ASIA_MINVER_OFFSET, ASIA_RSTICK_OFFSET);

            byte[] patched = ApplyPatch(plainFull, mainLines, plan.Reverse);

            // 目标是 PS 端（亚洲/欧美）时，结构修改后清零对应字段
            if (plan.IsUs && target == Plat.Us) ClearFields(patched, US_MINVER_OFFSET, US_RSTICK_OFFSET);
            if (!plan.IsUs && target == Plat.Asia) ClearFields(patched, ASIA_MINVER_OFFSET, ASIA_RSTICK_OFFSET);

            SAVE_SIZE = patched.Length;
            BODY_SIZE = SAVE_SIZE - 8;
            TRAILER_OFFSET = BODY_SIZE;
            byte[] fixedPlain = PatchChecksums(patched);

            byte[] rtchashNewBytes = new byte[8];
            Array.Copy(fixedPlain, RTCHASH_OFFSET, rtchashNewBytes, 0, 8);
            ulong rtchashNew = BitConverter.ToUInt64(rtchashNewBytes, 0);
            var (key_lo_new, key_hi_new) = DeriveKey(rtchashNew);

            byte[] bodyToEncrypt = fixedPlain.Take(BODY_SIZE).ToArray();
            byte[] encryptedBody = Encrypt(bodyToEncrypt, key_lo_new, key_hi_new, BODY_SIZE);
            byte[] newCipher = new byte[encryptedBody.Length + 8];
            Array.Copy(encryptedBody, newCipher, encryptedBody.Length);
            Array.Copy(rtchashNewBytes, 0, newCipher, encryptedBody.Length, 8);
            File.WriteAllBytes(Path.Combine(outputFolder, "SECURE.BIN"), newCipher);

            string[] cardLines = ReadPatchLines(plan.CardPatch);
            foreach (string card in Directory.GetFiles(outputFolder, "C*.BIN").Where(f => Path.GetFileName(f) != "SECURE.BIN"))
            {
                byte[] cardData = File.ReadAllBytes(card);
                File.WriteAllBytes(card, ApplyPatch(cardData, cardLines, plan.Reverse));
            }
        }

        // ===================== remotecache.vdf 生成 =====================
        static string RelativePath(string root, string file)
        {
            string rootFull = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            string fileFull = Path.GetFullPath(file);
            Uri rootUri = new Uri(rootFull);
            Uri fileUri = new Uri(fileFull);
            string rel = Uri.UnescapeDataString(rootUri.MakeRelativeUri(fileUri).ToString());
            return rel.Replace('/', '\\');
        }

        static string Sha1Hex(byte[] data)
        {
            using var sha = SHA1.Create();
            byte[] h = sha.ComputeHash(data);
            var sb = new StringBuilder(h.Length * 2);
            foreach (byte b in h) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        public static void GenerateRemoteCacheVdf(string remoteFolder)
        {
            var sb = new StringBuilder();
            sb.Append('"' + AppId.ToString() + "\"\r\n{\r\n");
            sb.Append("\t\"ChangeNumber\"\t\t\"0\"\r\n");
            sb.Append("\t\"OSType\"\t\t\"0\"\r\n");
            var files = Directory.GetFiles(remoteFolder, "*", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                string rel = RelativePath(remoteFolder, file).Replace('\\', '/');
                byte[] data = File.ReadAllBytes(file);
                long size = data.Length;
                string sha = Sha1Hex(data);
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                sb.Append("\t\"" + rel + "\"\r\n\t{\r\n");
                sb.Append("\t\t\"root\"\t\t\"0\"\r\n");
                sb.Append("\t\t\"size\"\t\t\"" + size.ToString() + "\"\r\n");
                sb.Append("\t\t\"localtime\"\t\t\"" + now.ToString() + "\"\r\n");
                sb.Append("\t\t\"time\"\t\t\"" + now.ToString() + "\"\r\n");
                sb.Append("\t\t\"remotetime\"\t\t\"0\"\r\n");
                sb.Append("\t\t\"sha\"\t\t\"" + sha + "\"\r\n");
                sb.Append("\t\t\"syncstate\"\t\t\"0\"\r\n");
                sb.Append("\t\t\"persiststate\"\t\t\"0\"\r\n");
                sb.Append("\t\t\"platformstosync2\"\t\t\"-1\"\r\n");
                sb.Append("\t}\r\n");
            }
            sb.Append("}\r\n");
            File.WriteAllText(Path.Combine(remoteFolder, "remotecache.vdf"), sb.ToString(), new UTF8Encoding());
        }

        // ===================== 自我测试（--selftest）=====================
        static void SelfTest()
        {
            string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory ?? ".", "selftest.log");
            var sbLog = new StringBuilder();
            void W(string s) { sbLog.AppendLine(s); Console.WriteLine(s); }
            string tmp = Path.Combine(Path.GetTempPath(), "GE2RB_selftest_" + Guid.NewGuid().ToString("N"));
            try
            {
                string src = Path.Combine(tmp, "save_ps");
                Directory.CreateDirectory(src);
                var rnd = new Random(12345);
                int size = 0x80050;
                byte[] body = new byte[size - 8];
                rnd.NextBytes(body);
                byte[] trailer = new byte[8];
                rnd.NextBytes(trailer);
                using (var fs = new FileStream(Path.Combine(src, "SECURE.BIN"), FileMode.Create))
                {
                    fs.Write(body, 0, body.Length);
                    fs.Write(trailer, 0, 8);
                }
                string dst = Path.Combine(tmp, "save_steam");
                ConvertFolderTo(src, dst, Plat.Steam, srcHint: Plat.Asia);
                long outSize = new FileInfo(Path.Combine(dst, "SECURE.BIN")).Length;
                W($"SELFTEST convert(亚洲版->Steam): in=0x{size:X} out=0x{outSize:X} (期望 0x90050) 结果={(outSize == 0x90050 ? "PASS" : "FAIL")}");

                string remote = Path.Combine(tmp, "remote");
                Directory.CreateDirectory(Path.Combine(remote, "GE211aa685531120001000"));
                File.WriteAllText(Path.Combine(remote, "GE211aa685531120001000", "SECURE.BIN"), "dummy-content");
                GenerateRemoteCacheVdf(remote);
                string vdf = File.ReadAllText(Path.Combine(remote, "remotecache.vdf"));
                W("SELFTEST vdf (前 14 行):");
                W(string.Join("\n", vdf.Split('\n').Take(14)));
                W("SELFTEST OK");
            }
            catch (Exception ex)
            {
                W("SELFTEST FAIL: " + ex);
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch { }
                try { File.WriteAllText(logPath, sbLog.ToString()); } catch { }
            }
        }

        [STAThread]
        static void Main()
        {
            if (Environment.GetCommandLineArgs().Skip(1).FirstOrDefault() == "--selftest")
            {
                SelfTest();
                return;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
