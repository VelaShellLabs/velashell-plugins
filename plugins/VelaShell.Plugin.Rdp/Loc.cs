using System.Globalization;

namespace VelaShell.Plugin.Rdp;

internal sealed class Loc(string locale)
{
    private readonly int _language = locale.ToLowerInvariant() switch
    {
        "zh-hant" or "zh-tw" or "zh-hk" => 2,
        var s when s.StartsWith("zh") => 1,
        var s when s.StartsWith("ja") => 3,
        var s when s.StartsWith("ko") => 4,
        _ => 0
    };

    public string this[string key] => Text[key][_language];
    public string Format(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, this[key], args);
    internal static IEnumerable<string> Keys => Text.Keys;

    private static readonly Dictionary<string, string[]> Text = new(StringComparer.Ordinal)
    {
        ["Host"] = ["Server address", "服务器地址", "伺服器位址", "サーバーアドレス", "서버 주소"],
        ["User"] = ["Username", "用户名", "使用者名稱", "ユーザー名", "사용자 이름"],
        ["Password"] = ["Password", "密码", "密碼", "パスワード", "암호"],
        ["Domain"] = ["Domain (optional)", "域（可选）", "網域（選填）", "ドメイン（任意）", "도메인 (선택)"],
        ["Width"] = ["Desktop width", "桌面宽度", "桌面寬度", "デスクトップの幅", "데스크톱 너비"],
        ["Height"] = ["Desktop height", "桌面高度", "桌面高度", "デスクトップの高さ", "데스크톱 높이"],
        ["Timeout"] = ["Connect timeout (seconds)", "连接超时（秒）", "連線逾時（秒）", "接続タイムアウト（秒）", "연결 제한 시간 (초)"],
        ["Clipboard"] = ["Bidirectional clipboard: text, images and files", "双向剪贴板：文本、图像与文件", "雙向剪貼簿：文字、影像與檔案", "双方向クリップボード：テキスト・画像・ファイル", "양방향 클립보드: 텍스트, 이미지 및 파일"],
        ["ClipboardHint"] = ["Disabled by default. Enabling allows this desktop to read and replace your clipboard, including files.", "默认关闭。启用后远程桌面可读取和替换本地剪贴板，包括文件。", "預設關閉。啟用後遠端桌面可讀取和取代本機剪貼簿，包括檔案。", "初期設定は無効です。有効にするとリモート側がファイルを含むクリップボードを読み書きできます。", "기본적으로 꺼져 있습니다. 사용하면 원격 데스크톱이 파일을 포함한 클립보드를 읽고 바꿀 수 있습니다."],
        ["DrivesPage"] = ["Drive mapping", "磁盘映射", "磁碟對應", "ドライブのマッピング", "드라이브 매핑"],
        ["Drives"] = ["Custom folders", "自定义文件夹", "自訂資料夾", "カスタム フォルダー", "사용자 지정 폴더"],
        ["DrivesHint"] = [
            "Optional. Format: name=full path of a local folder; separate several with semicolons (;).\ne.g.  {0}   or   {1}\nPick any name (1–8 letters, digits or _); the remote desktop sees it as \\\\tsclient\\work. Ticked disks and these folders can be read and written remotely.",
            "可选。格式:名称=本机文件夹的完整路径,多个用英文分号 ; 隔开。\n例如:{0}   或   {1}\n名称自己起(1–8 位字母、数字或下划线),远程桌面在 \\\\tsclient\\work 看到它。勾选的磁盘和这些文件夹,远程都可以读写。",
            "選填。格式:名稱=本機資料夾的完整路徑,多筆以英文分號 ; 隔開。\n例如:{0}   或   {1}\n名稱自行命名(1–8 個字母、數字或底線),遠端桌面在 \\\\tsclient\\work 看到它。勾選的磁碟與這些資料夾,遠端都可以讀寫。",
            "任意。形式:名前=ローカル フォルダーのフル パス。複数はセミコロン (;) で区切ります。\n例:{0}   または   {1}\n名前は自由に付けられます(1~8 文字の英数字または _)。リモート側では \\\\tsclient\\work として見えます。チェックしたディスクとこれらのフォルダーは、リモートから読み書きできます。",
            "선택 사항. 형식: 이름=로컬 폴더의 전체 경로. 여러 개는 세미콜론(;)으로 구분합니다.\n예: {0}   또는   {1}\n이름은 자유롭게 정합니다(1~8자의 영문자, 숫자, _). 원격 데스크톱에서는 \\\\tsclient\\work 로 보입니다. 선택한 디스크와 이 폴더들은 원격에서 읽고 쓸 수 있습니다."],
        ["DrivesInvalidEntry"] = [
            "Custom folder \"{0}\" is not valid. Write name=full path of an existing folder, e.g. {1} — the name is 1–8 letters, digits or underscores.",
            "自定义文件夹「{0}」写法不对。应写成 名称=已存在文件夹的完整路径,例如 {1};名称为 1–8 位字母、数字或下划线。",
            "自訂資料夾「{0}」寫法不正確。應寫成 名稱=已存在資料夾的完整路徑,例如 {1};名稱為 1–8 個字母、數字或底線。",
            "カスタム フォルダー「{0}」の書き方が正しくありません。名前=既存フォルダーのフル パス の形で書いてください。例:{1}(名前は 1~8 文字の英数字または _)。",
            "사용자 지정 폴더 \"{0}\" 형식이 올바르지 않습니다. 이름=존재하는 폴더의 전체 경로 형식으로 쓰세요. 예: {1} (이름은 1~8자의 영문자, 숫자, _)."],
        ["DrivesDuplicateName"] = [
            "The name \"{0}\" is used twice. Ticked disks take names too (C, E, root, home, …); pick another name for the custom folder.",
            "名称「{0}」重复了。勾选的磁盘也占用名称(如 C、E、root、home),请给自定义文件夹换个名称。",
            "名稱「{0}」重複了。勾選的磁碟也會占用名稱(如 C、E、root、home),請為自訂資料夾換個名稱。",
            "名前「{0}」が重複しています。チェックしたディスクも名前を使います(C、E、root、home など)。カスタム フォルダーには別の名前を付けてください。",
            "이름 \"{0}\"이(가) 중복됩니다. 선택한 디스크도 이름을 사용합니다(C, E, root, home 등). 사용자 지정 폴더에 다른 이름을 지정하세요."],
        ["DrivesTooMany"] = [
            "At most {0} disks and custom folders can be mapped in total.",
            "磁盘与自定义文件夹合计最多映射 {0} 个。",
            "磁碟與自訂資料夾合計最多對應 {0} 個。",
            "ディスクとカスタム フォルダーは合計 {0} 個までマッピングできます。",
            "디스크와 사용자 지정 폴더는 합쳐서 최대 {0}개까지 매핑할 수 있습니다."],
        ["DiskLocal"] = ["Local Disk", "本地磁盘", "本機磁碟", "ローカル ディスク", "로컬 디스크"],
        ["DiskRemovable"] = ["Removable Disk", "可移动磁盘", "卸除式磁碟", "リムーバブル ディスク", "이동식 디스크"],
        ["DiskNetwork"] = ["Network Drive", "网络驱动器", "網路磁碟機", "ネットワーク ドライブ", "네트워크 드라이브"],
        ["DiskOptical"] = ["CD/DVD Drive", "光盘驱动器", "光碟機", "CD/DVD ドライブ", "CD/DVD 드라이브"],
        ["DiskRoot"] = ["Root", "根目录", "根目錄", "ルート", "루트"],
        ["DiskHome"] = ["Home", "主目录", "主目錄", "ホーム", "홈"],
        ["Jump"] = ["SSH jump host (optional)", "SSH 跳板机（可选）", "SSH 跳板（選填）", "SSH 踏み台（任意）", "SSH 점프 호스트 (선택)"],
        ["Pin"] = ["Trusted certificate SHA-256", "已信任证书 SHA-256", "已信任憑證 SHA-256", "信頼済み証明書 SHA-256", "신뢰하는 인증서 SHA-256"],
        ["Connected"] = ["Connected", "已连接", "已連線", "接続済み", "연결됨"],
        ["Disconnected"] = ["Disconnected", "已断开连接", "已中斷連線", "切断されました", "연결 끊김"],
        ["Closed"] = ["Closed", "已关闭", "已關閉", "終了", "닫힘"],
        ["Send"] = ["Send clipboard", "发送剪贴板", "傳送剪貼簿", "クリップボードを送信", "클립보드 보내기"],
        ["Receive"] = ["Receive clipboard", "接收剪贴板", "接收剪貼簿", "クリップボードを受信", "클립보드 받기"],
        ["SecureAttention"] = ["Ctrl+Alt+Delete", "Ctrl+Alt+Delete", "Ctrl+Alt+Delete", "Ctrl+Alt+Delete", "Ctrl+Alt+Delete"],
        ["Fit"] = ["Fit desktop", "适应窗口", "符合視窗", "ウィンドウに合わせる", "창에 맞추기"],
        ["Actual"] = ["100%", "100%", "100%", "100%", "100%"],
        ["ReconnectButton"] = ["Reconnect", "重新连接", "重新連線", "再接続", "다시 연결"],
        ["Reconnecting"] = ["Reconnecting…", "正在重新连接…", "正在重新連線…", "再接続しています…", "다시 연결 중…"],
        ["ReconnectFailed"] = ["Reconnect failed. Try again.", "重新连接失败，请重试。", "重新連線失敗，請重試。", "再接続に失敗しました。もう一度お試しください。", "다시 연결에 실패했습니다. 다시 시도하세요."],
        ["Resolution"] = ["{0} × {1}", "{0} × {1}", "{0} × {1}", "{0} × {1}", "{0} × {1}"],
        ["Invalid"] = ["Invalid RDP setting: {0}", "RDP 设置无效：{0}", "RDP 設定無效：{0}", "RDP 設定が無効です：{0}", "잘못된 RDP 설정: {0}"],
        ["MissingNative"] = ["The RDP native library is missing or incompatible. Install the RDP package for {0} (ABI 1).", "缺少 RDP 原生库或版本不兼容。请安装适用于 {0} 的 RDP 插件包（ABI 1）。", "缺少 RDP 原生程式庫或版本不相容。請安裝適用於 {0} 的 RDP 外掛套件（ABI 1）。", "RDP ネイティブライブラリがないか互換性がありません。{0} 用パッケージ（ABI 1）をインストールしてください。", "RDP 네이티브 라이브러리가 없거나 호환되지 않습니다. {0}용 RDP 패키지(ABI 1)를 설치하세요."],
        ["AuthFailed"] = ["RDP authentication failed. Check your credentials and account permissions.", "RDP 认证失败。请检查凭据和账号权限。", "RDP 驗證失敗。請檢查認證資訊和帳號權限。", "RDP 認証に失敗しました。資格情報とアカウント権限を確認してください。", "RDP 인증 실패. 자격 증명과 계정 권한을 확인하세요."],
        ["ConnectFailed"] = ["RDP connection failed (0x{0:X8}).", "RDP 连接失败（0x{0:X8}）。", "RDP 連線失敗（0x{0:X8}）。", "RDP 接続に失敗しました（0x{0:X8}）。", "RDP 연결 실패 (0x{0:X8})."],
        ["ConnectTimedOut"] = ["RDP connection timed out.", "RDP 连接超时。", "RDP 連線逾時。", "RDP 接続がタイムアウトしました。", "RDP 연결 시간이 초과되었습니다."],
        ["Certificate"] = ["The RDP server certificate requires host approval.", "RDP 服务器证书需要宿主确认。", "RDP 伺服器憑證需要宿主確認。", "RDP サーバー証明書にはホストの承認が必要です。", "RDP 서버 인증서에 호스트 승인이 필요합니다."],
        ["Reconnect"] = ["Reopen this session through the host to renew credentials and certificate approval.", "请通过宿主重新打开会话，以更新凭据与证书信任。", "請透過宿主重新開啟工作階段，以更新認證資訊與憑證信任。", "資格情報と証明書の承認を更新するため、ホストからセッションを開き直してください。", "자격 증명과 인증서 승인을 갱신하려면 호스트에서 세션을 다시 여세요."],
        ["InputFailed"] = ["Remote input or clipboard operation failed.", "远程输入或剪贴板操作失败。", "遠端輸入或剪貼簿操作失敗。", "リモート入力またはクリップボード操作に失敗しました。", "원격 입력 또는 클립보드 작업에 실패했습니다."]
    };
}
