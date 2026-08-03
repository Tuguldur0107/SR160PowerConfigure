using System.Collections.Generic;

namespace SR160PowerConfig
{
    public static class Lang
    {
        public static string Current = "mn";

        public static readonly string[] Codes = { "mn", "en", "zh", "ru" };
        public static readonly string[] Names = { "Монгол", "English", "中文", "Русский" };

        private static readonly Dictionary<string, Dictionary<string, string>> Strings;

        static Lang()
        {
            Strings = new Dictionary<string, Dictionary<string, string>>();

            // ─── Mongolian (default) ───
            var mn = new Dictionary<string, string>();
            mn.Add("windowTitle",       "CHIPMO — SR160 Power Config");
            mn.Add("deviceDesc",        "Chainway SR160 UHF RFID — TX Power тохируулагч");
            mn.Add("grpConnection",     "USB холболт");
            mn.Add("statusDisconnected","● Салсан");
            mn.Add("statusConnected",   "● Холбогдсон");
            mn.Add("statusSearching","● Хайж байна...");
            mn.Add("btnConnect",        "Холбогдох");
            mn.Add("btnDisconnect",     "Салгах");
            mn.Add("currentPower",      "Одоогийн хүч:  —");
            mn.Add("currentPowerVal",   "Одоогийн хүч:  {0} dBm");
            mn.Add("currentPowerFail",  "Одоогийн хүч:  уншиж чадсангүй (код: {0})");
            mn.Add("btnRefresh",        "Шинэчлэх");
            mn.Add("grpPower",          "Хүч тохируулах (5 – 30 dBm)");
            mn.Add("btnSave",           "Хадгалах");
            mn.Add("lblEpcInput",       "EPC оролт:");
            mn.Add("btnClear",          "Цэвэрлэх");
            mn.Add("tagCount",          "Нийт: {0} таг");
            mn.Add("colEpc",            "EPC код");
            mn.Add("colCount",          "Тоо");
            mn.Add("errConnectMsg",     "Төхөөрөмжтэй холбогдож чадсангүй.\nАлдааны код: {0}");
            mn.Add("errTitle",          "Алдаа");
            mn.Add("updateTitle", "Шинэчлэлт");
            mn.Add("updateAvailable", "Шинэ хувилбар гарсан: {0}");
            mn.Add("updateCurrent", "Одоогийн хувилбар: {0}");
            mn.Add("updateAsk", "Одоо шинэчлэх үү?");
            mn.Add("errGeneric",        "Алдаа: {0}");
            mn.Add("successSaveMsg",    "Хүчийг {0} dBm болгож хадгаллаа.");
            mn.Add("successTitle",      "Амжилттай");
            mn.Add("errPowerMsg",       "Хүч тохируулж чадсангүй.\nАлдааны код: {0}");
            mn.Add("btnScan",          "Уншуулах");
            mn.Add("scanning",         "Уншиж байна...");
            mn.Add("grpScanMode",      "Скан горим");
            mn.Add("lblScanModePick",  "Горим:");
            mn.Add("lblAutoBehavior",  "Автомат:");
            mn.Add("modeSingle",       "Ганц");
            mn.Add("modeAuto",         "Автомат");
            mn.Add("modeAutoClick",    "Товших");
            mn.Add("modeAutoHold",     "Барих");
            mn.Add("chkBeep",          "Дуу");
            mn.Add("connTypeNone",     "Холболт: —");
            mn.Add("connTypeUsb",      "Холболт: USB");
            mn.Add("connTypeKeyboard", "Холболт: Гар (Bluetooth)");
            mn.Add("lblCooldown",      "Ижил таг дахин тоолох хугацаа (мс):");
            mn.Add("lblAutoStop", "Шинэ таг олдохгүй бол зогсох (сек, 0=үгүй):");
            mn.Add("chkRepeatKeepsAlive", "Дахин уншсан таг ч ажиллуулсаар байх");
            mn.Add("tabScan", "Скан");
            mn.Add("tabSetup", "Тохиргоо");
            mn.Add("tabAdvanced", "Нэмэлт");
            mn.Add("grpAdvanced", "Нэмэлт тохиргоо");
            mn.Add("chkClearOnScan", "Скан бүрийн эхэнд жагсаалт цэвэрлэх (ижил таг дахин бичих)");
            mn.Add("grpTrigger",       "Скайнерын товч ба гаралт");
            mn.Add("btnLearnTrigger",  "Скайнерын товч тохируулах");
            mn.Add("btnLearnTriggerWaiting", "Одоо товчийг дарна уу...");
            mn.Add("triggerDeviceNone", "Тохируулаагүй байна");
            mn.Add("triggerDeviceWaiting", "Скайнерын товчийг дарна уу...");
            mn.Add("triggerDeviceLearned", "Тохируулсан ({0})");
            mn.Add("lblExternalTargetPick", "Бичих цонх:");
            mn.Add("btnRefreshWindows", "Шинэчлэх");
            mn.Add("chkMinimizeToTray", "Хаахад tray-д ажиллуулах");
            mn.Add("chkStartWithWindows", "Windows-той хамт эхлүүлэх");
            mn.Add("trayShow", "Нээх");
            mn.Add("trayExit", "Гарах");
            mn.Add("externalTargetNone", "(Илгээхгүй)");
            mn.Add("targetRemembered", "Сануулсан: {0}");
            mn.Add("targetRememberedMissing", "Сануулсан: {0} (нээгдээгүй)");
            Strings.Add("mn", mn);

            // ─── English ───
            var en = new Dictionary<string, string>();
            en.Add("windowTitle",       "CHIPMO — SR160 Power Config");
            en.Add("deviceDesc",        "Chainway SR160 UHF RFID — TX Power Configurator");
            en.Add("grpConnection",     "USB Connection");
            en.Add("statusDisconnected","● Disconnected");
            en.Add("statusConnected",   "● Connected");
            en.Add("statusSearching","● Searching for reader...");
            en.Add("btnConnect",        "Connect");
            en.Add("btnDisconnect",     "Disconnect");
            en.Add("currentPower",      "Current Power:  —");
            en.Add("currentPowerVal",   "Current Power:  {0} dBm");
            en.Add("currentPowerFail",  "Current Power:  failed to read (code: {0})");
            en.Add("btnRefresh",        "Refresh");
            en.Add("grpPower",          "Adjust Power (5 – 30 dBm)");
            en.Add("btnSave",           "Save");
            en.Add("lblEpcInput",       "EPC Input:");
            en.Add("btnClear",          "Clear");
            en.Add("tagCount",          "Total: {0} tags");
            en.Add("colEpc",            "EPC Code");
            en.Add("colCount",          "Count");
            en.Add("errConnectMsg",     "Failed to connect to device.\nError code: {0}");
            en.Add("errTitle",          "Error");
            en.Add("updateTitle", "Update");
            en.Add("updateAvailable", "A new version is available: {0}");
            en.Add("updateCurrent", "You are running: {0}");
            en.Add("updateAsk", "Update now?");
            en.Add("errGeneric",        "Error: {0}");
            en.Add("successSaveMsg",    "Power saved as {0} dBm.");
            en.Add("successTitle",      "Success");
            en.Add("errPowerMsg",       "Failed to set power.\nError code: {0}");
            en.Add("btnScan",          "Scan");
            en.Add("scanning",         "Scanning...");
            en.Add("grpScanMode",      "Scan Mode");
            en.Add("lblScanModePick",  "Mode:");
            en.Add("lblAutoBehavior",  "Auto:");
            en.Add("modeSingle",       "Single");
            en.Add("modeAuto",         "Auto");
            en.Add("modeAutoClick",    "Toggle");
            en.Add("modeAutoHold",     "Hold");
            en.Add("chkBeep",          "Beep");
            en.Add("connTypeNone",     "Link: —");
            en.Add("connTypeUsb",      "Link: USB");
            en.Add("connTypeKeyboard", "Link: Keyboard (Bluetooth)");
            en.Add("lblCooldown",      "Re-count same tag after (ms):");
            en.Add("lblAutoStop", "Stop when no new tags (s, 0 = never):");
            en.Add("chkRepeatKeepsAlive", "Repeat reads also keep it running");
            en.Add("tabScan", "Scan");
            en.Add("tabSetup", "Setup");
            en.Add("tabAdvanced", "Advanced");
            en.Add("grpAdvanced", "Advanced options");
            en.Add("chkClearOnScan", "Clear list at start of each scan (rewrite same tag)");
            en.Add("grpTrigger",       "Scanner button & output");
            en.Add("btnLearnTrigger",  "Set up scanner button");
            en.Add("btnLearnTriggerWaiting", "Press the scanner button now...");
            en.Add("triggerDeviceNone", "Not set up yet");
            en.Add("triggerDeviceWaiting", "Waiting — press the scanner button...");
            en.Add("triggerDeviceLearned", "Ready ({0})");
            en.Add("lblExternalTargetPick", "Type into:");
            en.Add("btnRefreshWindows", "Refresh");
            en.Add("chkMinimizeToTray", "Keep running in tray");
            en.Add("chkStartWithWindows", "Start with Windows");
            en.Add("trayShow", "Show");
            en.Add("trayExit", "Exit");
            en.Add("externalTargetNone", "(Don't send)");
            en.Add("targetRemembered", "Remembered: {0}");
            en.Add("targetRememberedMissing", "Remembered: {0} (not open)");
            Strings.Add("en", en);

            // ─── Chinese (中文) ───
            var zh = new Dictionary<string, string>();
            zh.Add("windowTitle",       "CHIPMO — SR160 功率配置");
            zh.Add("deviceDesc",        "Chainway SR160 UHF RFID — TX 功率配置工具");
            zh.Add("grpConnection",     "USB 连接");
            zh.Add("statusDisconnected","● 已断开");
            zh.Add("statusConnected",   "● 已连接");
            zh.Add("statusSearching","● 正在查找读写器...");
            zh.Add("btnConnect",        "连接");
            zh.Add("btnDisconnect",     "断开");
            zh.Add("currentPower",      "当前功率：—");
            zh.Add("currentPowerVal",   "当前功率：{0} dBm");
            zh.Add("currentPowerFail",  "当前功率：读取失败（代码：{0}）");
            zh.Add("btnRefresh",        "刷新");
            zh.Add("grpPower",          "功率调节（5 – 30 dBm）");
            zh.Add("btnSave",           "保存");
            zh.Add("lblEpcInput",       "EPC 输入：");
            zh.Add("btnClear",          "清除");
            zh.Add("tagCount",          "合计：{0} 个标签");
            zh.Add("colEpc",            "EPC 编码");
            zh.Add("colCount",          "数量");
            zh.Add("errConnectMsg",     "无法连接到设备。\n错误代码：{0}");
            zh.Add("errTitle",          "错误");
            zh.Add("updateTitle", "更新");
            zh.Add("updateAvailable", "有新版本：{0}");
            zh.Add("updateCurrent", "当前版本：{0}");
            zh.Add("updateAsk", "现在更新吗？");
            zh.Add("errGeneric",        "错误：{0}");
            zh.Add("successSaveMsg",    "功率已保存为 {0} dBm。");
            zh.Add("successTitle",      "成功");
            zh.Add("errPowerMsg",       "无法设置功率。\n错误代码：{0}");
            zh.Add("btnScan",          "扫描");
            zh.Add("scanning",         "扫描中...");
            zh.Add("grpScanMode",      "扫描模式");
            zh.Add("lblScanModePick",  "模式：");
            zh.Add("lblAutoBehavior",  "自动：");
            zh.Add("modeSingle",       "单次");
            zh.Add("modeAuto",         "自动");
            zh.Add("modeAutoClick",    "切换");
            zh.Add("modeAutoHold",     "按住");
            zh.Add("chkBeep",          "蜂鸣");
            zh.Add("connTypeNone",     "连接：—");
            zh.Add("connTypeUsb",      "连接：USB");
            zh.Add("connTypeKeyboard", "连接：键盘（蓝牙）");
            zh.Add("lblCooldown",      "同一标签重复计数间隔 (毫秒)：");
            zh.Add("lblAutoStop", "无新标签时停止（秒，0=不停）：");
            zh.Add("chkRepeatKeepsAlive", "重复读取也保持运行");
            zh.Add("tabScan", "扫描");
            zh.Add("tabSetup", "设置");
            zh.Add("tabAdvanced", "高级");
            zh.Add("grpAdvanced", "高级选项");
            zh.Add("chkClearOnScan", "每次扫描开始时清空列表（重复标签可再次输出）");
            zh.Add("grpTrigger",       "扫描器按键与输出");
            zh.Add("btnLearnTrigger",  "设置扫描器按键");
            zh.Add("btnLearnTriggerWaiting", "请现在按下扫描器按键...");
            zh.Add("triggerDeviceNone", "尚未设置");
            zh.Add("triggerDeviceWaiting", "等待中 — 请按扫描器按键...");
            zh.Add("triggerDeviceLearned", "已就绪 ({0})");
            zh.Add("lblExternalTargetPick", "输出到窗口：");
            zh.Add("btnRefreshWindows", "刷新");
            zh.Add("chkMinimizeToTray", "最小化到托盘继续运行");
            zh.Add("chkStartWithWindows", "开机自动启动");
            zh.Add("trayShow", "显示");
            zh.Add("trayExit", "退出");
            zh.Add("externalTargetNone", "（不发送）");
            zh.Add("targetRemembered", "已记住：{0}");
            zh.Add("targetRememberedMissing", "已记住：{0}（未打开）");
            Strings.Add("zh", zh);

            // ─── Russian (Русский) ───
            var ru = new Dictionary<string, string>();
            ru.Add("windowTitle",       "CHIPMO — SR160 Power Config");
            ru.Add("deviceDesc",        "Chainway SR160 UHF RFID — Настройка мощности TX");
            ru.Add("grpConnection",     "USB подключение");
            ru.Add("statusDisconnected","● Отключено");
            ru.Add("statusConnected",   "● Подключено");
            ru.Add("statusSearching","● Поиск считывателя...");
            ru.Add("btnConnect",        "Подключить");
            ru.Add("btnDisconnect",     "Отключить");
            ru.Add("currentPower",      "Текущая мощность:  —");
            ru.Add("currentPowerVal",   "Текущая мощность:  {0} дБм");
            ru.Add("currentPowerFail",  "Текущая мощность:  не удалось прочитать (код: {0})");
            ru.Add("btnRefresh",        "Обновить");
            ru.Add("grpPower",          "Настройка мощности (5 – 30 дБм)");
            ru.Add("btnSave",           "Сохранить");
            ru.Add("lblEpcInput",       "EPC ввод:");
            ru.Add("btnClear",          "Очистить");
            ru.Add("tagCount",          "Всего: {0} меток");
            ru.Add("colEpc",            "EPC код");
            ru.Add("colCount",          "Кол-во");
            ru.Add("errConnectMsg",     "Не удалось подключиться к устройству.\nКод ошибки: {0}");
            ru.Add("errTitle",          "Ошибка");
            ru.Add("updateTitle", "Обновление");
            ru.Add("updateAvailable", "Доступна новая версия: {0}");
            ru.Add("updateCurrent", "Текущая версия: {0}");
            ru.Add("updateAsk", "Обновить сейчас?");
            ru.Add("errGeneric",        "Ошибка: {0}");
            ru.Add("successSaveMsg",    "Мощность сохранена как {0} дБм.");
            ru.Add("successTitle",      "Успешно");
            ru.Add("errPowerMsg",       "Не удалось установить мощность.\nКод ошибки: {0}");
            ru.Add("btnScan",          "Сканировать");
            ru.Add("scanning",         "Сканирование...");
            ru.Add("grpScanMode",      "Режим сканирования");
            ru.Add("lblScanModePick",  "Режим:");
            ru.Add("lblAutoBehavior",  "Авто:");
            ru.Add("modeSingle",       "Один");
            ru.Add("modeAuto",         "Авто");
            ru.Add("modeAutoClick",    "Клик");
            ru.Add("modeAutoHold",     "Держать");
            ru.Add("chkBeep",          "Звук");
            ru.Add("connTypeNone",     "Связь: —");
            ru.Add("connTypeUsb",      "Связь: USB");
            ru.Add("connTypeKeyboard", "Связь: Клавиатура (Bluetooth)");
            ru.Add("lblCooldown",      "Повторный счёт того же тега (мс):");
            ru.Add("lblAutoStop", "Стоп, если нет новых тегов (с, 0=никогда):");
            ru.Add("chkRepeatKeepsAlive", "Повторные чтения тоже продлевают");
            ru.Add("tabScan", "Скан");
            ru.Add("tabSetup", "Настройки");
            ru.Add("tabAdvanced", "Дополнительно");
            ru.Add("grpAdvanced", "Дополнительные параметры");
            ru.Add("chkClearOnScan", "Очищать список в начале каждого скана (повтор тега)");
            ru.Add("grpTrigger",       "Кнопка сканера и вывод");
            ru.Add("btnLearnTrigger",  "Настроить кнопку сканера");
            ru.Add("btnLearnTriggerWaiting", "Нажмите кнопку сканера...");
            ru.Add("triggerDeviceNone", "Не настроено");
            ru.Add("triggerDeviceWaiting", "Ожидание — нажмите кнопку сканера...");
            ru.Add("triggerDeviceLearned", "Готово ({0})");
            ru.Add("lblExternalTargetPick", "Писать в окно:");
            ru.Add("btnRefreshWindows", "Обновить");
            ru.Add("chkMinimizeToTray", "Работать в трее");
            ru.Add("chkStartWithWindows", "Запускать с Windows");
            ru.Add("trayShow", "Показать");
            ru.Add("trayExit", "Выход");
            ru.Add("externalTargetNone", "(Не отправлять)");
            ru.Add("targetRemembered", "Запомнено: {0}");
            ru.Add("targetRememberedMissing", "Запомнено: {0} (не открыто)");
            Strings.Add("ru", ru);
        }

        public static string Get(string key)
        {
            if (Strings.ContainsKey(Current) && Strings[Current].ContainsKey(key))
                return Strings[Current][key];
            return Strings["mn"][key];
        }

        public static string Get(string key, object arg0)
        {
            return string.Format(Get(key), arg0);
        }
    }
}
