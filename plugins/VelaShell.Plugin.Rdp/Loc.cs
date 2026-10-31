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
        ["Drives"] = ["Shared folders", "共享文件夹", "共用資料夾", "共有フォルダー", "공유 폴더"],
        ["DrivesHint"] = ["Empty by default. One name=absolute-path per line; the remote desktop receives read/write access to each folder.", "默认空。每行 name=绝对路径；远程桌面可读写这些文件夹。", "預設空白。每行 name=絕對路徑；遠端桌面可讀寫這些資料夾。", "初期設定は空です。1行に name=絶対パス。リモート側に各フォルダーの読み書きを許可します。", "기본값은 비어 있습니다. 줄마다 name=절대경로. 원격 데스크톱이 각 폴더를 읽고 쓸 수 있습니다."],
        ["Jump"] = ["SSH jump host (optional)", "SSH 跳板机（可选）", "SSH 跳板（選填）", "SSH 踏み台（任意）", "SSH 점프 호스트 (선택)"],
        ["Pin"] = ["Trusted certificate SHA-256", "已信任证书 SHA-256", "已信任憑證 SHA-256", "信頼済み証明書 SHA-256", "신뢰하는 인증서 SHA-256"],
        ["Connected"] = ["Connected", "已连接", "已連線", "接続済み", "연결됨"],
        ["Disconnected"] = ["Disconnected — reconnect from the session tab", "已断开，请从会话标签重新连接", "已中斷，請從工作階段頁籤重新連線", "切断されました。セッションタブから再接続してください", "연결 끊김 — 세션 탭에서 다시 연결하세요"],
        ["Closed"] = ["Closed", "已关闭", "已關閉", "終了", "닫힘"],
        ["Send"] = ["Send clipboard", "发送剪贴板", "傳送剪貼簿", "クリップボードを送信", "클립보드 보내기"],
        ["Receive"] = ["Receive clipboard", "接收剪贴板", "接收剪貼簿", "クリップボードを受信", "클립보드 받기"],
        ["SecureAttention"] = ["Ctrl+Alt+Delete", "Ctrl+Alt+Delete", "Ctrl+Alt+Delete", "Ctrl+Alt+Delete", "Ctrl+Alt+Delete"],
        ["Fit"] = ["Fit desktop", "适应窗口", "符合視窗", "ウィンドウに合わせる", "창에 맞추기"],
        ["Actual"] = ["100%", "100%", "100%", "100%", "100%"],
        ["Resolution"] = ["{0} × {1} · fixed session resolution", "{0} × {1} · 固定会话分辨率", "{0} × {1} · 固定工作階段解析度", "{0} × {1} · 固定セッション解像度", "{0} × {1} · 고정 세션 해상도"],
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
