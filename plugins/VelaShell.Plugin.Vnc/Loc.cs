using System.Globalization;

namespace VelaShell.Plugin.Vnc;

/// <summary>The plugin's own strings: English, Simplified Chinese, Traditional Chinese, Japanese, Korean (same set as the host).</summary>
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
    internal static IEnumerable<string[]> Rows => Text.Values;

    private static readonly Dictionary<string, string[]> Text = new(StringComparer.Ordinal)
    {
        // ---- connection form ----
        ["Host"] = ["Server address", "服务器地址", "伺服器位址", "サーバーアドレス", "서버 주소"],
        ["User"] = ["Username (VeNCrypt / macOS only)", "用户名（仅 VeNCrypt / macOS）", "使用者名稱（僅 VeNCrypt / macOS）", "ユーザー名（VeNCrypt / macOS のみ）", "사용자 이름 (VeNCrypt / macOS 전용)"],
        ["Password"] = ["Password", "密码", "密碼", "パスワード", "암호"],
        ["DisplayPage"] = ["Display", "显示", "顯示", "表示", "디스플레이"],
        ["Security"] = ["Security", "安全类型", "安全性類型", "セキュリティ", "보안 유형"],
        ["SecurityHint"] = [
            "Auto picks the strongest type the server offers: VeNCrypt TLS, Apple Remote Desktop (with a username), VNC password, then none. Classic VNC passwords use at most 8 characters and are not encrypted in transit; use TLS or an SSH jump host on untrusted networks.",
            "自动：选服务器提供的最强类型——VeNCrypt TLS、Apple 远程桌面（需填用户名）、VNC 密码，最后才是无认证。传统 VNC 密码最多 8 个字符，传输过程不加密；在不可信网络上请使用 TLS 或 SSH 跳板。",
            "自動：選伺服器提供的最強類型——VeNCrypt TLS、Apple 遠端桌面（需填使用者名稱）、VNC 密碼，最後才是無驗證。傳統 VNC 密碼最多 8 個字元，傳輸過程不加密；在不受信任的網路上請使用 TLS 或 SSH 跳板。",
            "自動：サーバーが提供する最も強い方式を選びます（VeNCrypt TLS、Apple リモートデスクトップ（ユーザー名が必要）、VNC パスワード、最後に認証なし）。従来の VNC パスワードは最大 8 文字で、通信は暗号化されません。信頼できないネットワークでは TLS か SSH 踏み台を使ってください。",
            "자동: 서버가 제공하는 가장 강력한 유형을 선택합니다(VeNCrypt TLS, Apple 원격 데스크톱(사용자 이름 필요), VNC 암호, 마지막으로 인증 없음). 기존 VNC 암호는 최대 8자이며 전송 중 암호화되지 않습니다. 신뢰할 수 없는 네트워크에서는 TLS 또는 SSH 점프 호스트를 사용하세요."],
        ["SecurityAuto"] = ["Auto (recommended)", "自动（推荐）", "自動（建議）", "自動（推奨）", "자동 (권장)"],
        ["SecurityNone"] = ["None", "无认证", "無驗證", "認証なし", "인증 없음"],
        ["SecurityVnc"] = ["VNC password", "VNC 密码", "VNC 密碼", "VNC パスワード", "VNC 암호"],
        ["SecurityVeNCrypt"] = ["VeNCrypt (TLS + X.509)", "VeNCrypt（TLS + X.509）", "VeNCrypt（TLS + X.509）", "VeNCrypt（TLS + X.509）", "VeNCrypt (TLS + X.509)"],
        ["SecurityArd"] = ["Apple Remote Desktop (macOS)", "Apple 远程桌面（macOS）", "Apple 遠端桌面（macOS）", "Apple リモートデスクトップ（macOS）", "Apple 원격 데스크톱 (macOS)"],
        ["Shared"] = ["Share the desktop with other viewers", "与其他查看者共享桌面", "與其他檢視者共用桌面", "他のビューアーとデスクトップを共有", "다른 뷰어와 데스크톱 공유"],
        ["SharedHint"] = [
            "Unticked asks the server to disconnect everyone else who is viewing this desktop.",
            "取消勾选会要求服务器断开正在查看此桌面的其他所有人。",
            "取消勾選會要求伺服器中斷正在檢視此桌面的其他所有人。",
            "オフにすると、このデスクトップを表示している他のユーザーを切断するようサーバーに求めます。",
            "선택을 해제하면 이 데스크톱을 보고 있는 다른 모든 사람의 연결을 끊도록 서버에 요청합니다."],
        ["ViewOnly"] = ["View only (no keyboard or mouse)", "仅查看（不发送键盘和鼠标）", "僅檢視（不傳送鍵盤和滑鼠）", "表示のみ（キーボードとマウスを送らない）", "보기 전용 (키보드와 마우스 보내지 않음)"],
        ["Clipboard"] = ["Bidirectional clipboard (text)", "双向剪贴板（文本）", "雙向剪貼簿（文字）", "双方向クリップボード（テキスト）", "양방향 클립보드 (텍스트)"],
        ["ClipboardHint"] = [
            "Enabled by default. The remote desktop can read the text you copy locally and replace your clipboard text. Servers with the extended clipboard exchange Unicode; older servers only Latin-1.",
            "默认开启。远程桌面可读取你在本机复制的文本，也可替换本地剪贴板文本。支持扩展剪贴板的服务器可交换 Unicode 文本，旧服务器只支持 Latin-1。",
            "預設開啟。遠端桌面可讀取你在本機複製的文字，也可取代本機剪貼簿文字。支援延伸剪貼簿的伺服器可交換 Unicode 文字，舊伺服器只支援 Latin-1。",
            "初期設定は有効です。リモート デスクトップはローカルでコピーしたテキストを読み取り、クリップボードのテキストを置き換えられます。拡張クリップボード対応サーバーは Unicode、古いサーバーは Latin-1 のみです。",
            "기본적으로 켜져 있습니다. 원격 데스크톱이 로컬에서 복사한 텍스트를 읽고 클립보드 텍스트를 바꿀 수 있습니다. 확장 클립보드를 지원하는 서버는 유니코드를, 이전 서버는 Latin-1만 주고받습니다."],
        ["Scaling"] = ["Display size", "显示尺寸", "顯示大小", "表示サイズ", "표시 크기"],
        ["ScalingHint"] = [
            "Match window resizes the remote desktop to the tab when the server allows it (for example TigerVNC) and scales it otherwise.",
            "「匹配窗口」会在服务器允许时把远程桌面调整为标签页大小（例如 TigerVNC），否则缩放显示。",
            "「符合視窗」會在伺服器允許時把遠端桌面調整為索引標籤大小（例如 TigerVNC），否則縮放顯示。",
            "「ウィンドウに合わせる」は、サーバーが許可すればリモート デスクトップをタブの大きさに変更し（TigerVNC など）、そうでなければ拡大縮小します。",
            "'창에 맞춤'은 서버가 허용하면 원격 데스크톱 크기를 탭에 맞게 바꾸고(예: TigerVNC), 그렇지 않으면 크기를 조정해 표시합니다."],
        ["ScalingRemote"] = ["Match window", "匹配窗口", "符合視窗", "ウィンドウに合わせる", "창에 맞춤"],
        ["ScalingFit"] = ["Scale to fit", "缩放以适应", "縮放以符合", "拡大縮小して収める", "맞게 크기 조정"],
        ["ScalingNone"] = ["Actual size (scroll)", "实际大小（可滚动）", "實際大小（可捲動）", "実寸（スクロール）", "실제 크기 (스크롤)"],
        ["Quality"] = ["Picture quality", "画质", "畫質", "画質", "화질"],
        ["QualityHint"] = [
            "With Tight encoding the server may send photos and video as JPEG. Lossless never uses JPEG; lower quality saves bandwidth on slow links.",
            "使用 Tight 编码时，服务器可把照片和视频以 JPEG 发送。「无损」从不使用 JPEG；画质越低越省带宽，适合慢速链路。",
            "使用 Tight 編碼時，伺服器可把相片和影片以 JPEG 傳送。「無損」從不使用 JPEG；畫質越低越省頻寬，適合慢速連線。",
            "Tight エンコードでは、サーバーが写真や動画を JPEG で送ることがあります。「ロスレス」は JPEG を使いません。画質を下げると低速回線で帯域を節約できます。",
            "Tight 인코딩에서는 서버가 사진과 동영상을 JPEG로 보낼 수 있습니다. '무손실'은 JPEG를 쓰지 않으며, 화질을 낮추면 느린 회선에서 대역폭을 아낄 수 있습니다."],
        ["QualityLossless"] = ["Lossless", "无损", "無損", "ロスレス", "무손실"],
        ["QualityHigh"] = ["High", "高", "高", "高", "높음"],
        ["QualityMedium"] = ["Medium", "中", "中", "中", "보통"],
        ["QualityLow"] = ["Low (slow networks)", "低（慢速网络）", "低（慢速網路）", "低（低速ネットワーク）", "낮음 (느린 네트워크)"],
        ["Encoding"] = ["Preferred encoding", "首选编码", "偏好編碼", "優先エンコード", "기본 인코딩"],
        ["EncodingHint"] = [
            "The server may still pick another encoding from the list. Raw is for diagnosing a server whose compressed encodings misbehave.",
            "服务器仍可能从列表中挑选其他编码。Raw 用于诊断压缩编码有问题的服务器。",
            "伺服器仍可能從清單中挑選其他編碼。Raw 用於診斷壓縮編碼有問題的伺服器。",
            "サーバーはリスト内の別のエンコードを選ぶこともあります。Raw は圧縮エンコードに問題があるサーバーの診断用です。",
            "서버는 목록에서 다른 인코딩을 선택할 수도 있습니다. Raw는 압축 인코딩에 문제가 있는 서버를 진단할 때 사용합니다."],
        ["EncodingAuto"] = ["Auto (Tight)", "自动（Tight）", "自動（Tight）", "自動（Tight）", "자동 (Tight)"],
        ["Compression"] = ["Compression level (0–9)", "压缩级别（0–9）", "壓縮層級（0–9）", "圧縮レベル（0–9）", "압축 수준 (0–9)"],
        ["CompressionHint"] = [
            "Higher levels use less bandwidth and more server CPU. 2 suits most networks.",
            "级别越高越省带宽，服务器 CPU 占用越多。多数网络用 2 即可。",
            "層級越高越省頻寬，伺服器 CPU 使用越多。多數網路用 2 即可。",
            "高いほど帯域を節約し、サーバーの CPU を使います。ほとんどのネットワークでは 2 で十分です。",
            "수준이 높을수록 대역폭은 줄고 서버 CPU 사용은 늘어납니다. 대부분의 네트워크에는 2가 적당합니다."],
        ["MacCommand"] = ["Command key on macOS", "macOS 上的 Command 键", "macOS 上的 Command 鍵", "macOS の Command キー", "macOS의 Command 키"],
        ["MacCommandHint"] = [
            "Sent as Ctrl so Cmd+C / Cmd+V copy and paste on a Windows or Linux desktop; choose Super when the remote is a Mac.",
            "按 Ctrl 发送，Cmd+C / Cmd+V 即可在 Windows 或 Linux 桌面上复制粘贴；远端是 Mac 时选 Super。",
            "以 Ctrl 傳送，Cmd+C / Cmd+V 即可在 Windows 或 Linux 桌面上複製貼上；遠端是 Mac 時選 Super。",
            "Ctrl として送るため、Windows や Linux のデスクトップで Cmd+C / Cmd+V がコピーと貼り付けになります。リモートが Mac の場合は Super を選んでください。",
            "Ctrl로 보내므로 Windows나 Linux 데스크톱에서 Cmd+C / Cmd+V로 복사와 붙여넣기를 할 수 있습니다. 원격이 Mac이면 Super를 선택하세요."],
        ["MacCommandCtrl"] = ["Send as Ctrl", "按 Ctrl 发送", "以 Ctrl 傳送", "Ctrl として送る", "Ctrl로 보내기"],
        ["MacCommandSuper"] = ["Send as Super / ⌘", "按 Super / ⌘ 发送", "以 Super / ⌘ 傳送", "Super / ⌘ として送る", "Super / ⌘로 보내기"],
        ["Timeout"] = ["Connect timeout (seconds)", "连接超时（秒）", "連線逾時（秒）", "接続タイムアウト（秒）", "연결 제한 시간 (초)"],
        ["Jump"] = ["SSH jump host (optional)", "SSH 跳板机（可选）", "SSH 跳板（選填）", "SSH 踏み台（任意）", "SSH 점프 호스트 (선택)"],
        ["Pin"] = ["Trusted certificate SHA-256", "已信任证书 SHA-256", "已信任憑證 SHA-256", "信頼済み証明書 SHA-256", "신뢰하는 인증서 SHA-256"],

        // ---- connection errors ----
        ["Invalid"] = ["Invalid VNC setting: {0}", "VNC 设置无效：{0}", "VNC 設定無效：{0}", "VNC 設定が無効です：{0}", "잘못된 VNC 설정: {0}"],
        ["ConnectTimedOut"] = ["VNC connection timed out.", "VNC 连接超时。", "VNC 連線逾時。", "VNC 接続がタイムアウトしました。", "VNC 연결 시간이 초과되었습니다."],
        ["AuthFailed"] = ["VNC authentication failed. Check the password.", "VNC 认证失败，请检查密码。", "VNC 驗證失敗，請檢查密碼。", "VNC 認証に失敗しました。パスワードを確認してください。", "VNC 인증 실패. 암호를 확인하세요."],
        ["AuthFailedReason"] = ["VNC authentication failed: {0}", "VNC 认证失败：{0}", "VNC 驗證失敗：{0}", "VNC 認証に失敗しました：{0}", "VNC 인증 실패: {0}"],
        ["PasswordRequired"] = ["This VNC server requires a password.", "此 VNC 服务器需要密码。", "此 VNC 伺服器需要密碼。", "この VNC サーバーにはパスワードが必要です。", "이 VNC 서버에는 암호가 필요합니다."],
        ["CredentialsRequired"] = ["This VNC server requires a username and password.", "此 VNC 服务器需要用户名和密码。", "此 VNC 伺服器需要使用者名稱和密碼。", "この VNC サーバーにはユーザー名とパスワードが必要です。", "이 VNC 서버에는 사용자 이름과 암호가 필요합니다."],
        ["SecurityUnsupported"] = [
            "The VNC server offers no security type this client can use ({0}). Enable VNC password, VeNCrypt with X.509 TLS, or None on the server.",
            "VNC 服务器提供的安全类型均无法使用（{0}）。请在服务器上启用 VNC 密码、带 X.509 证书的 VeNCrypt TLS 或无认证。",
            "VNC 伺服器提供的安全性類型均無法使用（{0}）。請在伺服器上啟用 VNC 密碼、搭配 X.509 憑證的 VeNCrypt TLS 或無驗證。",
            "VNC サーバーが提供するセキュリティ方式はどれも使えません（{0}）。サーバーで VNC パスワード、X.509 証明書付きの VeNCrypt TLS、または認証なしを有効にしてください。",
            "VNC 서버가 제공하는 보안 유형을 사용할 수 없습니다({0}). 서버에서 VNC 암호, X.509 인증서를 쓰는 VeNCrypt TLS 또는 인증 없음을 켜세요."],
        ["SecurityNotOffered"] = [
            "The VNC server does not offer the chosen security type (it offers {0}). Choose Auto or another type.",
            "VNC 服务器不提供所选的安全类型（它提供 {0}）。请选择「自动」或其他类型。",
            "VNC 伺服器不提供所選的安全性類型（它提供 {0}）。請選擇「自動」或其他類型。",
            "VNC サーバーは選んだセキュリティ方式を提供していません（提供されているのは {0}）。「自動」か別の方式を選んでください。",
            "VNC 서버가 선택한 보안 유형을 제공하지 않습니다(제공: {0}). '자동' 또는 다른 유형을 선택하세요."],
        ["Certificate"] = ["The VNC server's TLS certificate requires approval.", "VNC 服务器的 TLS 证书需要确认。", "VNC 伺服器的 TLS 憑證需要確認。", "VNC サーバーの TLS 証明書には承認が必要です。", "VNC 서버의 TLS 인증서에 승인이 필요합니다."],
        ["CertificateChanged"] = [
            "The VNC server's certificate is different from the one you approved before. Trust the new certificate only if you expected the change.",
            "VNC 服务器证书与之前信任的证书不同。只有在预期会变更时才信任新证书。",
            "VNC 伺服器憑證與先前信任的憑證不同。僅在預期會變更時才信任新憑證。",
            "VNC サーバーの証明書が以前に承認したものと異なります。変更が想定どおりの場合にのみ新しい証明書を信頼してください。",
            "VNC 서버 인증서가 이전에 승인한 인증서와 다릅니다. 변경이 예상된 경우에만 새 인증서를 신뢰하세요."],
        ["ConnectFailedAt"] = ["VNC connection failed ({0}): {1}", "VNC 连接失败（{0}）：{1}", "VNC 連線失敗（{0}）：{1}", "VNC 接続に失敗しました（{0}）：{1}", "VNC 연결 실패 ({0}): {1}"],
        ["StageTransport"] = ["connecting", "建立连接", "建立連線", "接続", "연결"],
        ["StageProtocolVersion"] = ["protocol version", "协议版本", "通訊協定版本", "プロトコル バージョン", "프로토콜 버전"],
        ["StageSecurity"] = ["security negotiation", "安全协商", "安全性交涉", "セキュリティ ネゴシエーション", "보안 협상"],
        ["StageTls"] = ["TLS handshake", "TLS 握手", "TLS 交握", "TLS ハンドシェイク", "TLS 핸드셰이크"],
        ["StageAuthentication"] = ["authentication", "认证", "驗證", "認証", "인증"],
        ["StageInitialization"] = ["initialization", "初始化", "初始化", "初期化", "초기화"],
        ["ByeServerClosed"] = [
            "The VNC server closed the connection (the session ended, or another viewer took it over).",
            "VNC 服务器关闭了连接（会话已结束，或被其他查看者接管）。",
            "VNC 伺服器關閉了連線（工作階段已結束，或被其他檢視者接管）。",
            "VNC サーバーが接続を閉じました（セッションが終了したか、別のビューアーに引き継がれました）。",
            "VNC 서버가 연결을 닫았습니다(세션이 끝났거나 다른 뷰어가 가져갔습니다)."],
        ["ByeProtocol"] = ["The VNC session ended because of a protocol error: {0}", "VNC 会话因协议错误而结束：{0}", "VNC 工作階段因通訊協定錯誤而結束：{0}", "プロトコル エラーのため VNC セッションが終了しました：{0}", "프로토콜 오류로 VNC 세션이 종료되었습니다: {0}"],

        // ---- workspace view ----
        ["Connected"] = ["Connected", "已连接", "已連線", "接続済み", "연결됨"],
        ["Disconnected"] = ["Disconnected", "已断开连接", "已中斷連線", "切断されました", "연결 끊김"],
        ["Closed"] = ["Closed", "已关闭", "已關閉", "終了", "닫힘"],
        ["Resolution"] = ["{0} × {1}", "{0} × {1}", "{0} × {1}", "{0} × {1}", "{0} × {1}"],
        ["ViewOnlyStatus"] = ["View only", "仅查看", "僅檢視", "表示のみ", "보기 전용"],
        ["InputFailed"] = ["Remote input or clipboard operation failed.", "远程输入或剪贴板操作失败。", "遠端輸入或剪貼簿操作失敗。", "リモート入力またはクリップボード操作に失敗しました。", "원격 입력 또는 클립보드 작업에 실패했습니다."],
        ["OverlayClosedTitle"] = ["Connection Lost", "连接已断开", "連線已中斷", "接続が切断されました", "연결 끊김"],
        ["OverlayFailedTitle"] = ["Connection Failed", "连接失败", "連線失敗", "接続に失敗しました", "연결 실패"],
        ["DismissOverlay"] = ["Dismiss Notice", "关闭提示", "關閉提示", "通知を閉じる", "알림 닫기"],
        ["CopyError"] = ["Copy", "复制", "複製", "コピー", "복사"],
        ["ErrorCopied"] = ["Copied", "已复制", "已複製", "コピーしました", "복사됨"],
        ["CopyErrorTip"] = ["Copy error message", "复制错误信息", "複製錯誤訊息", "エラーメッセージをコピー", "오류 메시지 복사"],
        ["ReconnectButton"] = ["Reconnect", "重新连接", "重新連線", "再接続", "다시 연결"],
        ["Reconnecting"] = ["Reconnecting…", "正在重新连接…", "正在重新連線…", "再接続しています…", "다시 연결 중…"],
        ["ReconnectFailed"] = ["Reconnect failed. Try again.", "重新连接失败，请重试。", "重新連線失敗，請重試。", "再接続に失敗しました。もう一度お試しください。", "다시 연결에 실패했습니다. 다시 시도하세요."],
        ["Reconnect"] = ["Reopen this session through the host to renew credentials and certificate approval.", "请通过宿主重新打开会话，以更新凭据与证书信任。", "請透過宿主重新開啟工作階段，以更新認證資訊與憑證信任。", "資格情報と証明書の承認を更新するため、ホストからセッションを開き直してください。", "자격 증명과 인증서 승인을 갱신하려면 호스트에서 세션을 다시 여세요."],
        ["AutoReconnecting"] = [
            "The network connection was lost. Reconnecting automatically (attempt {0} of {1})…",
            "网络连接已中断，正在自动重新连接（第 {0}/{1} 次）…",
            "網路連線已中斷，正在自動重新連線（第 {0}/{1} 次）…",
            "ネットワーク接続が切断されました。自動的に再接続しています（{0}/{1} 回目）…",
            "네트워크 연결이 끊어졌습니다. 자동으로 다시 연결하는 중입니다({0}/{1}번째)…"],
        ["AutoReconnectFailed"] = [
            "The network connection was lost and could not be restored automatically.",
            "网络连接已中断，自动重新连接未成功。",
            "網路連線已中斷，自動重新連線未成功。",
            "ネットワーク接続が切断され、自動再接続できませんでした。",
            "네트워크 연결이 끊어졌으며 자동으로 다시 연결하지 못했습니다."],

        // ---- remote keys ----
        ["QuickBall"] = ["Remote keys", "远程按键", "遠端按鍵", "リモートキー", "원격 키"],
        ["QuickArmed"] = ["Click again", "再点确认", "再點確認", "もう一度", "다시 클릭"],
        ["QuickSecureAttention"] = ["Ctrl+Alt+Del", "Ctrl+Alt+Del", "Ctrl+Alt+Del", "Ctrl+Alt+Del", "Ctrl+Alt+Del"],
        ["QuickSwitchWindow"] = ["Switch window", "切换窗口", "切換視窗", "ウィンドウ切替", "창 전환"],
        ["QuickCloseWindow"] = ["Close window", "关闭窗口", "關閉視窗", "ウィンドウを閉じる", "창 닫기"],
        ["QuickTypeClipboard"] = ["Type clipboard", "键入剪贴板", "鍵入剪貼簿", "クリップ入力", "클립보드 입력"],
        ["QuickTypeClipboardTip"] = [
            "Type the local clipboard text as keystrokes — for sign-in screens, consoles and servers without clipboard support",
            "把本机剪贴板里的文本逐字键入远端——用于登录界面、控制台以及不支持剪贴板的服务器",
            "把本機剪貼簿裡的文字逐字鍵入遠端——用於登入畫面、主控台以及不支援剪貼簿的伺服器",
            "ローカルのクリップボードの文字をキー入力として送信します（サインイン画面、コンソール、クリップボード非対応のサーバー向け）",
            "로컬 클립보드 텍스트를 키 입력으로 보냅니다(로그인 화면, 콘솔, 클립보드를 지원하지 않는 서버용)"],
        ["QuickStart"] = ["Super / Win", "Super / Win", "Super / Win", "Super / Win", "Super / Win"],
        ["QuickRun"] = ["Run", "运行", "執行", "実行", "실행"],
        ["QuickLock"] = ["Lock", "锁定", "鎖定", "ロック", "잠금"],
        ["QuickPrintScreen"] = ["Screenshot", "截屏", "擷取畫面", "画面キャプチャ", "화면 캡처"],
        ["TypeClipboardEmpty"] = ["The clipboard has no text to type.", "剪贴板里没有可键入的文本。", "剪貼簿裡沒有可鍵入的文字。", "クリップボードに入力できる文字がありません。", "클립보드에 입력할 텍스트가 없습니다."],
        ["TypeClipboardUnavailable"] = ["Couldn't read the clipboard.", "无法读取剪贴板。", "無法讀取剪貼簿。", "クリップボードを読み取れませんでした。", "클립보드를 읽을 수 없습니다."],
        ["TypeClipboardTooLong"] = ["The clipboard text is too long to type ({0} characters, limit {1}).", "剪贴板文本过长，未键入（{0} 个字符，上限 {1}）。", "剪貼簿文字過長，未鍵入（{0} 個字元，上限 {1}）。", "クリップボードの文字が長すぎるため入力しません（{0} 文字、上限 {1}）。", "클립보드 텍스트가 너무 길어 입력하지 않았습니다({0}자, 최대 {1}자)."],
        ["TypeClipboardConfirm"] = ["Type {0} characters into the remote? Click \"Type clipboard\" again to confirm.", "将向远端键入 {0} 个字符。再点一次「键入剪贴板」确认。", "將向遠端鍵入 {0} 個字元。再點一次「鍵入剪貼簿」確認。", "リモートに {0} 文字を入力します。もう一度「クリップ入力」をクリックして確定してください。", "원격에 {0}자를 입력합니다. 확인하려면 \"클립보드 입력\"을 다시 클릭하세요."],
        ["TypeClipboardConfirmLines"] = [
            "Type {0} characters into the remote? Line breaks are sent as Enter, which runs commands in a console. Click \"Type clipboard\" again to confirm.",
            "将向远端键入 {0} 个字符，其中的换行会按回车发送（在控制台里会执行命令）。再点一次「键入剪贴板」确认。",
            "將向遠端鍵入 {0} 個字元，其中的換行會以 Enter 送出（在主控台裡會執行命令）。再點一次「鍵入剪貼簿」確認。",
            "リモートに {0} 文字を入力します。改行は Enter として送られ、コンソールではコマンドが実行されます。もう一度「クリップ入力」をクリックして確定してください。",
            "원격에 {0}자를 입력합니다. 줄 바꿈은 Enter로 전송되어 콘솔에서는 명령이 실행됩니다. 확인하려면 \"클립보드 입력\"을 다시 클릭하세요."]
    };
}
