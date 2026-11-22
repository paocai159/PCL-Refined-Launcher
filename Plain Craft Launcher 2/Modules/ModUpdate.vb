Option Strict On
Option Explicit On

' ============================================================================
'  PCL 启动器自更新核心模块（原版 / 官方渠道）
'  与 PCL 原有的 ModNet / NetTask 解耦，独立可用，也可直接并入 PCL 源码树。
'  作者：DSH Agent        适配：.NET Framework 4.8 / VB 14
' ============================================================================

Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.IO
Imports System.IO.Compression
Imports System.Linq
Imports System.Net
Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.RegularExpressions
Imports System.Threading

''' <summary>一次可用的更新包信息。</summary>
Public Class UpdateInfo
    ''' <summary>目标版本号，例如 "2.9.1.0"。</summary>
    Public Property Version As String = ""
    ''' <summary>更新包下载地址（原始地址，未经过镜像）。</summary>
    Public Property DownloadUrl As String = ""
    ''' <summary>更新包文件名，例如 "PCL.exe" / "PCL.zip"。</summary>
    Public Property FileName As String = ""
    ''' <summary>SHA-256（小写十六进制）。为空表示服务端未提供，只能做尺寸校验。</summary>
    Public Property Sha256 As String = ""
    ''' <summary>更新包字节数，0 表示未知。</summary>
    Public Property Size As Long = 0
    ''' <summary>更新说明（Markdown / 纯文本）。</summary>
    Public Property Changelog As String = ""
    ''' <summary>是否必须更新（不回退到“稍后”）。</summary>
    Public Property Mandatory As Boolean = False
    ''' <summary>整数版本号（某些更新源用递增的 VersionCode 判断新旧，0 表示未提供）。</summary>
    Public Property VersionCode As Integer = 0

    Public Overrides Function ToString() As String
        Return Version & "  " & FileName & "  " & DownloadUrl
    End Function
End Class

''' <summary>更新过程事件：stage 取值 Check / Download / Verify / Apply。</summary>
Public Delegate Sub UpdateProgressHandler(stage As String, percent As Integer, message As String)

''' <summary>更新失败异常。</summary>
Public Class UpdateException
    Inherits Exception
    Public Sub New(message As String)
        MyBase.New(message)
    End Sub
    Public Sub New(message As String, inner As Exception)
        MyBase.New(message, inner)
    End Sub
End Class

''' <summary>更新包完整性校验失败（换镜像重试无意义，调用方应直接放弃本次更新）。</summary>
Public Class UpdateVerifyException
    Inherits UpdateException
    Public Sub New(message As String)
        MyBase.New(message)
    End Sub
End Class

Public Module ModUpdate

#Region "配置"

    ''' <summary>当前版本号，接入 PCL 时替换为 ModBase.VersionName。</summary>
    Public Property CurrentVersion As String = "2.9.0.0"

    ''' <summary>当前整数版本号，配合更新源的 VersionCode 使用；0 表示只按版本名字符串比较。</summary>
    Public Property CurrentVersionCode As Integer = 0

    ''' <summary>更新信息源，按顺序尝试。支持 GitHub Releases API 与简单 manifest JSON 两种格式。</summary>
    Public Property InfoSources As New List(Of String) From {
        "https://api.github.com/repos/Meloong-Git/PCL/releases/latest",
        "https://ghproxy.net/https://api.github.com/repos/Meloong-Git/PCL/releases/latest",
        "https://gh-proxy.com/https://api.github.com/repos/Meloong-Git/PCL/releases/latest"
    }

    ''' <summary>下载镜像前缀，按顺序尝试（直连始终排在最前）。</summary>
    Public Property DownloadMirrors As New List(Of String) From {
        "",
        "https://ghproxy.net/",
        "https://gh-proxy.com/",
        "https://ghfast.top/"
    }

    ''' <summary>网络请求 User-Agent，GitHub API 强制要求。</summary>
    Public Property UserAgent As String = "PCL2-Updater/1.0"

    ''' <summary>
    ''' GitHub Token（可选）。匿名访问 api.github.com 只有 60 次/小时，遇到 403 限流时可填入。
    ''' 为空时依次读取环境变量 PCL_UPDATE_TOKEN、GITHUB_TOKEN。
    ''' </summary>
    Public Property GitHubToken As String = ""

    ''' <summary>单次 HTTP 请求超时（毫秒）。</summary>
    Public Property RequestTimeout As Integer = 20000

    ''' <summary>失败重试次数（每个镜像）。</summary>
    Public Property RetryCount As Integer = 2

    ''' <summary>重启后传给新版本的参数，用于识别“刚刚完成更新”。</summary>
    Public Property RestartArgs As String = "--updated"

    ''' <summary>
    ''' 重启时是否附带 "版本号" 参数。接 PCL 官方旧版逻辑（--update &lt;pid&gt; &lt;old&gt; &lt;new&gt; &lt;restart&gt;）时应设为 False，
    ''' 否则参数个数对不上会在启动时抛异常。
    ''' </summary>
    Public Property PassUpdatedVersion As Boolean = True

    ''' <summary>更新暂存目录。</summary>
    Public ReadOnly Property StagingRoot As String
        Get
            Return Path.Combine(Path.GetTempPath(), "PCL", "Update")
        End Get
    End Property

    ''' <summary>当前进程可执行文件路径。</summary>
    Public ReadOnly Property ExePath As String
        Get
            Try
                Return Diagnostics.Process.GetCurrentProcess().MainModule.FileName
            Catch
                Return Reflection.Assembly.GetEntryAssembly().Location
            End Try
        End Get
    End Property

#End Region

#Region "版本比对"

    ''' <summary>比较两个版本号：a &gt; b 返回 1，a = b 返回 0，a &lt; b 返回 -1。支持 "2.9.1"、"2.9.1.0"、"2.9.1-beta.2"。</summary>
    Public Function CompareVersion(a As String, b As String) As Integer
        If String.IsNullOrWhiteSpace(a) AndAlso String.IsNullOrWhiteSpace(b) Then Return 0
        If String.IsNullOrWhiteSpace(a) Then Return -1
        If String.IsNullOrWhiteSpace(b) Then Return 1

        Dim pa = ParseVersion(a), pb = ParseVersion(b)
        Dim n = Math.Max(pa.Numbers.Length, pb.Numbers.Length)
        For i = 0 To n - 1
            Dim x = If(i < pa.Numbers.Length, pa.Numbers(i), 0)
            Dim y = If(i < pb.Numbers.Length, pb.Numbers(i), 0)
            If x <> y Then Return If(x > y, 1, -1)
        Next
        '数字段相同，再比较预发布标记：正式版（无后缀）大于预发布，预发布之间按字典序
        If pa.Pre = pb.Pre Then Return 0
        If pa.Pre.Length = 0 Then Return 1
        If pb.Pre.Length = 0 Then Return -1
        Return Math.Sign(String.CompareOrdinal(pa.Pre, pb.Pre))
    End Function

    Private Structure ParsedVersion
        Public Numbers As Integer()
        Public Pre As String
    End Structure

    Private Function ParseVersion(v As String) As ParsedVersion
        Dim s = v.Trim().TrimStart("v"c, "V"c)
        '丢弃语义化版本的 build 元数据（+ 之后不参与比较）
        Dim plusIdx = s.IndexOf("+"c)
        If plusIdx >= 0 Then s = s.Substring(0, plusIdx)
        Dim cut = s.IndexOf("-"c)
        Dim pre As String = ""
        If cut >= 0 Then
            pre = s.Substring(cut + 1)
            s = s.Substring(0, cut)
        End If
        Dim nums = s.Split("."c).
            Select(Function(t)
                       Dim r As Integer
                       If Integer.TryParse(t.Trim(), r) Then Return r Else Return 0
                   End Function).ToArray()
        Dim res As New ParsedVersion With {.Numbers = nums, .Pre = pre}
        Return res
    End Function

    ''' <summary>
    ''' 把 "主.次.修订.编译" 形式的版本号换算成整数（0.0.5.1 → 501），用于和服务端的 VersionCode 比较。
    ''' 只取前四段，缺的段按 0 处理。
    ''' </summary>
    Public Function VersionCodeFromVersionString(v As String) As Integer
        If String.IsNullOrWhiteSpace(v) Then Return 0
        Dim s = v.Trim().TrimStart("v"c, "V"c)
        Dim plusIdx = s.IndexOf("+"c)
        If plusIdx >= 0 Then s = s.Substring(0, plusIdx)
        Dim cut = s.IndexOf("-"c)
        If cut >= 0 Then s = s.Substring(0, cut)
        Dim nums(3) As Integer
        Dim parts = s.Split("."c)
        For i = 0 To Math.Min(3, parts.Length - 1)
            Integer.TryParse(parts(i).Trim(), nums(i))
        Next
        Return nums(0) * 1000000 + nums(1) * 10000 + nums(2) * 100 + nums(3)
    End Function

    ''' <summary>判断 info 是否比当前版本更新。</summary>
    Public Function IsNewer(info As UpdateInfo) As Boolean
        If info Is Nothing Then Return False
        '优先用整数版本号比较（更新源提供 VersionCode 时最可靠，例如 501 > 499）
        If info.VersionCode > 0 AndAlso CurrentVersionCode > 0 Then Return info.VersionCode > CurrentVersionCode
        Return CompareVersion(info.Version, CurrentVersion) > 0
    End Function

#End Region

#Region "检查更新"

    ''' <summary>拉取最新版本信息（阻塞，请在后台线程调用）。失败抛出 <see cref="UpdateException"/>。</summary>
    Public Function GetLatestInfo() As UpdateInfo
        Dim errors As New List(Of String)
        For Each src In InfoSources
            If String.IsNullOrWhiteSpace(src) Then Continue For
            For attempt = 0 To RetryCount
                Try
                    Dim body = HttpGetString(src)
                    Dim info = ParseInfo(body)
                    If info Is Nothing OrElse String.IsNullOrWhiteSpace(info.DownloadUrl) Then
                        Throw New UpdateException("更新源返回的内容无法解析：" & src)
                    End If
                    Return info
                Catch ex As Exception
                    errors.Add(src & "（第 " & (attempt + 1) & " 次）: " & ex.Message)
                End Try
                Thread.Sleep(400 * (attempt + 1))
            Next
        Next
        Throw New UpdateException("无法连接更新服务器。" & vbCrLf & String.Join(vbCrLf, errors.Take(4)))
    End Function

    ''' <summary>解析 GitHub Releases JSON 或简单 manifest JSON。</summary>
    Public Function ParseInfo(json As String) As UpdateInfo
        If String.IsNullOrWhiteSpace(json) Then Return Nothing
        Dim info As New UpdateInfo

        If json.IndexOf("""assets""", StringComparison.Ordinal) >= 0 Then
            '---- GitHub Releases API ----
            info.Version = JsonValue(json, "tag_name")
            info.Changelog = JsonValue(json, "body")

            Dim assets = JsonArrayRegion(json, "assets")
            If assets Is Nothing Then Return info
            Dim best As String = Nothing
            Dim bestRank = -1
            For Each obj In SplitObjects(assets)
                Dim name = JsonValue(obj, "name")
                Dim url = JsonValue(obj, "browser_download_url")
                If String.IsNullOrWhiteSpace(url) Then Continue For
                Dim rank = RankAsset(name)
                If rank > bestRank Then
                    bestRank = rank
                    best = obj
                End If
            Next
            If best Is Nothing Then Return info
            info.FileName = JsonValue(best, "name")
            info.DownloadUrl = JsonValue(best, "browser_download_url")
            info.Sha256 = NormalizeHash(JsonValue(best, "digest"))
            If String.IsNullOrWhiteSpace(info.Sha256) Then info.Sha256 = NormalizeHash(JsonValue(best, "sha256"))
            Dim size As Long
            If Long.TryParse(JsonValue(best, "size"), size) Then info.Size = size
        Else
            '---- 简单 manifest ----
            info.Version = JsonValue(json, "version")
            If String.IsNullOrWhiteSpace(info.Version) Then info.Version = JsonValue(json, "versionName") '兼容 PCL Refined Launcher 的 version.json
            info.DownloadUrl = JsonValue(json, "url")
            If String.IsNullOrWhiteSpace(info.DownloadUrl) Then info.DownloadUrl = JsonValue(json, "download_url")
            If String.IsNullOrWhiteSpace(info.DownloadUrl) Then info.DownloadUrl = JsonValue(json, "downloadUrl")
            info.FileName = JsonValue(json, "file_name")
            info.Sha256 = NormalizeHash(JsonValue(json, "sha256"))
            info.Changelog = JsonValue(json, "changelog")
            If String.IsNullOrWhiteSpace(info.Changelog) Then info.Changelog = JsonValue(json, "updateLog")
            info.Mandatory = JsonValue(json, "mandatory").ToLowerInvariant() = "true" OrElse
                            JsonValue(json, "isForce").ToLowerInvariant() = "true"
            Dim vc As Integer
            If Integer.TryParse(JsonValue(json, "versionCode"), vc) Then info.VersionCode = vc
            Dim size As Long
            If Long.TryParse(JsonValue(json, "size"), size) Then info.Size = size
        End If

        If String.IsNullOrWhiteSpace(info.FileName) Then
            '没写 file_name 时从下载地址推断，否则 zip 会被当成未知类型
            Try
                Dim guess = SanitizeUrl(info.DownloadUrl)
                Dim q = guess.IndexOf("?"c)
                If q >= 0 Then guess = guess.Substring(0, q)
                Dim slash = guess.LastIndexOf("/"c)
                If slash >= 0 AndAlso slash < guess.Length - 1 Then guess = guess.Substring(slash + 1)
                If guess.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) OrElse guess.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) Then
                    info.FileName = guess
                End If
            Catch
            End Try
        End If
        If String.IsNullOrWhiteSpace(info.FileName) Then
            info.FileName = "PCL.exe"
        End If
        If String.IsNullOrWhiteSpace(info.Sha256) Then info.Sha256 = ""
        Return info
    End Function

    ''' <summary>更新包优先级：单文件 exe 最高（可原地热替换），zip 次之。</summary>
    Private Function RankAsset(name As String) As Integer
        If String.IsNullOrWhiteSpace(name) Then Return -1
        Dim n = name.ToLowerInvariant()
        If Not n.EndsWith(".exe") AndAlso Not n.EndsWith(".zip") Then Return -1
        Dim rank = If(n.EndsWith(".exe"), 100, 60)
        If n.Contains("pcl") Then rank += 10
        If n.Contains("setup") OrElse n.Contains("install") Then rank -= 30
        Return rank
    End Function

    Private Function NormalizeHash(v As String) As String
        If String.IsNullOrWhiteSpace(v) Then Return ""
        v = v.Trim().ToLowerInvariant()
        If v.StartsWith("sha256:") Then v = v.Substring(7)
        If v.Length = 64 AndAlso v.All(Function(c) "0123456789abcdef".IndexOf(c) >= 0) Then Return v
        Return ""
    End Function

#End Region

#Region "下载与校验"

    ''' <summary>下载更新包到暂存目录并校验，返回本地路径（阻塞，请在后台线程调用）。</summary>
    Public Function Download(info As UpdateInfo, progress As UpdateProgressHandler,
                             Optional ct As CancellationToken = Nothing) As String
        Dim dir = Path.Combine(StagingRoot, SafeName(info.Version))
        Directory.CreateDirectory(dir)
        Dim fileName = If(String.IsNullOrWhiteSpace(info.FileName), "update.bin", info.FileName)
        Dim local = Path.Combine(dir, SafeName(fileName))

        Dim errors As New List(Of String)
        Dim cleanUrl = SanitizeUrl(info.DownloadUrl)
        '加速镜像只对 GitHub 地址有意义；其它域名直连，避免把镜像前缀硬套到第三方链接上
        Dim mirrors As List(Of String) = DownloadMirrors
        If Not cleanUrl.ToLowerInvariant().Contains("github") Then mirrors = New List(Of String) From {""}
        For Each prefix In mirrors
            If ct.IsCancellationRequested Then Throw New OperationCanceledException()
            Dim url = prefix & cleanUrl
            For attempt = 0 To RetryCount
                Try
                    DownloadFile(url, local, info.Size, progress, ct)
                    If progress IsNot Nothing Then progress.Invoke("Verify", 100, "正在校验文件完整性…")
                    Verify(local, info)
                    Return local
                Catch ex As OperationCanceledException
                    Throw
                Catch ex As UpdateVerifyException
                    '完整性校验失败：换个镜像下载同一份坏包也没用，直接放弃
                    Try
                        If File.Exists(local) Then File.Delete(local)
                    Catch
                    End Try
                    Throw
                Catch ex As Exception
                    errors.Add(If(prefix = "", "直连", prefix) & "（第 " & (attempt + 1) & " 次）: " & ex.Message)
                    Try
                        If File.Exists(local) Then File.Delete(local)
                    Catch
                    End Try
                    If ct.IsCancellationRequested Then Throw New OperationCanceledException()
                    Thread.Sleep(500 * (attempt + 1))
                End Try
            Next
        Next
        Throw New UpdateException("下载更新包失败。" & vbCrLf & String.Join(vbCrLf, errors.Take(4)))
    End Function

    Private Sub DownloadFile(url As String, local As String, total As Long,
                             progress As UpdateProgressHandler, ct As CancellationToken)
        EnsureTls()
        Using wc As New WebClient()
            wc.Headers.Add("User-Agent", UserAgent)
            wc.Headers.Add("Accept", "application/octet-stream")
            Dim finished As Boolean = False
            Dim err As Exception = Nothing
            Dim speedWatch = Diagnostics.Stopwatch.StartNew()

            AddHandler wc.DownloadProgressChanged,
                Sub(s, e)
                    Dim pct = 0
                    If e.TotalBytesToReceive > 0 Then
                        pct = CInt(Math.Min(99, e.BytesReceived * 100.0 / e.TotalBytesToReceive))
                    ElseIf total > 0 Then
                        pct = CInt(Math.Min(99, e.BytesReceived * 100.0 / total))
                    End If
                    Dim sizeText = FormatSize(e.BytesReceived) & " / " & FormatSize(If(e.TotalBytesToReceive > 0, e.TotalBytesToReceive, total))
                    Dim speedText = ""
                    If speedWatch.ElapsedMilliseconds > 400 Then
                        Dim mbps = e.BytesReceived / 1024.0 / 1024.0 / Math.Max(0.001, speedWatch.Elapsed.TotalSeconds)
                        speedText = "（" & mbps.ToString("0.0") & " MB/s）"
                    End If
                    If progress IsNot Nothing Then progress.Invoke("Download", pct, "正在下载更新包：" & sizeText & speedText)
                End Sub
            AddHandler wc.DownloadFileCompleted,
                Sub(s, e)
                    If e.Error IsNot Nothing Then err = e.Error
                    If e.Cancelled Then err = New OperationCanceledException()
                    finished = True
                End Sub

            Dim reg As CancellationTokenRegistration = Nothing
            If ct.CanBeCanceled Then
                reg = ct.Register(Sub()
                                      Try
                                          wc.CancelAsync()
                                      Catch
                                      End Try
                                  End Sub)
            End If

            Try
                If progress IsNot Nothing Then progress.Invoke("Download", 0, "正在连接下载服务器…")
                wc.DownloadFileAsync(New Uri(url), local)
                Dim sw = Diagnostics.Stopwatch.StartNew()
                Dim deadlineMs = Math.Max(60000, RequestTimeout * 10L)
                While Not finished
                    If ct.IsCancellationRequested Then
                        Try
                            wc.CancelAsync()
                        Catch
                        End Try
                        Throw New OperationCanceledException()
                    End If
                    If sw.ElapsedMilliseconds > deadlineMs Then
                        Try
                            wc.CancelAsync()
                        Catch
                        End Try
                        Throw New UpdateException("下载超时。")
                    End If
                    Thread.Sleep(80)
                End While
                If err IsNot Nothing Then Throw err
            Finally
                reg.Dispose()
            End Try
        End Using
    End Sub

    Private Sub Verify(local As String, info As UpdateInfo)
        If Not File.Exists(local) Then Throw New UpdateVerifyException("更新包未下载成功。")
        Dim len As Long = New FileInfo(local).Length
        If len <= 0 Then Throw New UpdateVerifyException("更新包为空。")
        If info.Size > 0 AndAlso len <> info.Size Then
            Throw New UpdateVerifyException("更新包大小不符：应为 " & info.Size & " 字节，实际 " & len & " 字节。")
        End If
        '内容特征校验：链接写错时服务器可能返回网页，绝不能拿它覆盖启动器
        ValidatePackage(local, info.FileName)
        If Not String.IsNullOrWhiteSpace(info.Sha256) Then
            Dim actual = Sha256File(local)
            If Not String.Equals(actual, info.Sha256, StringComparison.OrdinalIgnoreCase) Then
                Throw New UpdateVerifyException("更新包校验失败（SHA-256 不匹配）。" & vbCrLf & "期望：" & info.Sha256 & vbCrLf & "实际：" & actual)
            End If
        End If
    End Sub

    ''' <summary>按扩展名检查文件头，避免把 HTML 错误页当成更新包覆盖掉启动器。</summary>
    Private Sub ValidatePackage(local As String, fileName As String)
        Dim name = If(fileName, "").ToLowerInvariant()
        Dim head(3) As Byte
        Dim n As Integer
        Using fs = File.OpenRead(local)
            n = fs.Read(head, 0, head.Length)
        End Using
        If n < 2 Then Throw New UpdateVerifyException("更新包内容不完整。")
        If name.EndsWith(".exe") OrElse name.EndsWith(".dll") Then
            If head(0) <> &H4D OrElse head(1) <> &H5A Then ' “MZ”
                Throw New UpdateVerifyException("下载到的不是可执行文件，可能是网页或错误页（下载链接可能失效，或需要提取码）。")
            End If
        ElseIf name.EndsWith(".zip") Then
            If head(0) <> &H50 OrElse head(1) <> &H4B Then ' “PK”
                Throw New UpdateVerifyException("下载到的不是有效的 zip 压缩包，可能是网页或错误页（下载链接可能失效）。")
            End If
        ElseIf head(0) = &H3C Then ' “<”，基本可以断定是 HTML
            Throw New UpdateVerifyException("下载到的是网页而不是文件（下载链接可能失效，或需要提取码）。")
        End If
    End Sub

    ''' <summary>清洗下载地址：去掉逗号后的说明文字（例如“密码: axz1”）、首尾空白与引号。</summary>
    Public Function SanitizeUrl(url As String) As String
        If String.IsNullOrWhiteSpace(url) Then Return ""
        Dim s = url.Trim()
        Dim cut = s.IndexOf(","c)
        If cut > 0 Then s = s.Substring(0, cut)
        Return s.Trim().Trim(""""c).Trim()
    End Function

    ''' <summary>计算文件 SHA-256（小写十六进制）。</summary>
    Public Function Sha256File(path As String) As String
        Using fs = File.OpenRead(path)
            Using sha = SHA256.Create()
                Dim hash = sha.ComputeHash(fs)
                Dim sb As New StringBuilder(64)
                For Each b In hash
                    sb.Append(b.ToString("x2"))
                Next
                Return sb.ToString()
            End Using
        End Using
    End Function

#End Region

#Region "应用更新"

    ''' <summary>
    ''' 把已下载的更新包应用到当前程序，并启动新版本。
    ''' 单文件 exe 走“原地热替换”（先改名再复制，Windows 允许重命名运行中的 exe）；
    ''' zip 走“退出后由批处理替换”的路径。返回 True 表示已安排重启，调用方应关闭自身。
    ''' </summary>
    Public Function ApplyAndRestart(info As UpdateInfo, stagedFile As String,
                                    Optional progress As UpdateProgressHandler = Nothing) As Boolean
        Dim target = ExePath
        If Not File.Exists(target) Then Throw New UpdateException("找不到当前程序路径：" & target)

        Dim isZip = stagedFile.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
        If Not isZip AndAlso stagedFile.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) Then
            '---- 单文件：原地热替换 ----
            If progress IsNot Nothing Then progress.Invoke("Apply", 0, "正在替换程序文件…")
            Dim old = target & ".old"
            Try
                If File.Exists(old) Then File.Delete(old)
            Catch
            End Try
            File.Move(target, old)                 '重命名运行中的 exe 是允许的
            Try
                File.Copy(stagedFile, target, True)
            Catch ex As Exception
                '回滚
                Try
                    If File.Exists(target) Then File.Delete(target)
                    File.Move(old, target)
                Catch
                End Try
                Throw New UpdateException("替换程序文件失败，已回滚。" & vbCrLf & ex.Message, ex)
            End Try
            If progress IsNot Nothing Then progress.Invoke("Apply", 100, "替换完成，正在重启启动器…")
            StartDetached(target, BuildRestartArgs(info))
            Return True
        ElseIf isZip Then
            '---- 压缩包：解压后由批处理在进程退出后覆盖 ----
            Dim extractDir = Path.Combine(Path.GetDirectoryName(stagedFile), "extracted")
            If Directory.Exists(extractDir) Then Directory.Delete(extractDir, True)
            If progress IsNot Nothing Then progress.Invoke("Extract", 0, "正在解压更新包…")
            Dim exeInZip = ExtractPackage(stagedFile, extractDir, progress)
            If progress IsNot Nothing Then progress.Invoke("Extract", 100, "解压完成，正在检查启动器文件…")
            If exeInZip Is Nothing Then Throw New UpdateException("更新包内未找到启动器可执行文件。")

            If progress IsNot Nothing Then progress.Invoke("Apply", 0, "正在准备替换文件…")
            Dim script = WriteSwapScript(extractDir, Path.GetDirectoryName(exeInZip), target, BuildRestartArgs(info))
            Dim psi As New ProcessStartInfo("cmd.exe", "/c """ & script & """") With {
                .UseShellExecute = False,
                .CreateNoWindow = True,
                .WindowStyle = ProcessWindowStyle.Hidden
            }
            If progress IsNot Nothing Then progress.Invoke("Apply", 100, "启动器即将退出，自动替换并重启…")
            Diagnostics.Process.Start(psi)
            Return True
        End If
        Throw New UpdateException("不支持的更新包类型：" & Path.GetExtension(stagedFile))
    End Function

    ''' <summary>
    ''' 解压更新包到指定目录，并返回包内找到的启动器可执行文件路径（找不到返回 Nothing）。
    ''' </summary>
    Public Function ExtractPackage(zipPath As String, extractDir As String,
                                   Optional progress As UpdateProgressHandler = Nothing) As String
        ExtractZip(zipPath, extractDir, progress)
        Return FindLauncherExe(extractDir)
    End Function

    ''' <summary>解压更新包，并按条目数汇报进度（大小包都能看到“操作进程”）。</summary>
    Private Sub ExtractZip(zipPath As String, extractDir As String, progress As UpdateProgressHandler)
        Directory.CreateDirectory(extractDir)
        Using archive = IO.Compression.ZipFile.OpenRead(zipPath)
            Dim total = archive.Entries.Count
            Dim done As Integer = 0
            For Each entry In archive.Entries
                Dim relative = entry.FullName.Replace("/"c, "\"c)
                Dim dest = Path.Combine(extractDir, relative)
                If String.IsNullOrEmpty(entry.Name) Then
                    Directory.CreateDirectory(dest)
                Else
                    Dim parent = Path.GetDirectoryName(dest)
                    If Not String.IsNullOrEmpty(parent) Then Directory.CreateDirectory(parent)
                    Try
                        entry.ExtractToFile(dest, True)
                    Catch ex As Exception
                        Throw New UpdateException("解压失败（" & entry.FullName & "）：" & ex.Message, ex)
                    End Try
                End If
                done += 1
                If progress IsNot Nothing AndAlso total > 0 Then
                    progress.Invoke("Extract", CInt(done * 100.0 / total), "正在解压：" & entry.FullName)
                End If
            Next
        End Using
    End Sub

    Private Function BuildRestartArgs(info As UpdateInfo) As String
        Dim args = RestartArgs
        If Not PassUpdatedVersion Then Return If(args, "")
        If String.IsNullOrWhiteSpace(args) Then args = "--updated"
        Return args & " """ & info.Version & """"
    End Function

    Private Function FindLauncherExe(root As String) As String
        Dim candidates = Directory.GetFiles(root, "*.exe", SearchOption.AllDirectories)
        Return candidates.
            OrderByDescending(Function(p) If(Path.GetFileName(p).ToLowerInvariant().Contains("pcl"), 100, 0)).
            ThenByDescending(Function(p) Path.GetFileName(p).ToLowerInvariant().Contains("launcher")).
            FirstOrDefault()
    End Function

    ''' <summary>生成“等待本进程退出 → 覆盖文件 → 启动新版本 → 自删”的批处理。</summary>
    Private Function WriteSwapScript(extractDir As String, srcRoot As String, target As String, args As String) As String
        Dim pid = Diagnostics.Process.GetCurrentProcess().Id
        Dim script = Path.Combine(StagingRoot, "apply_update.cmd")
        Directory.CreateDirectory(StagingRoot)
        Dim sb As New StringBuilder()
        sb.AppendLine("@echo off")
        sb.AppendLine("chcp 65001 >nul")
        sb.AppendLine("setlocal")
        sb.AppendLine(":waitloop")
        sb.AppendLine("tasklist /FI ""PID eq " & pid & """ 2>nul | find """ & pid & """ >nul")
        sb.AppendLine("if not errorlevel 1 (")
        sb.AppendLine("  timeout /t 1 /nobreak >nul")
        sb.AppendLine("  goto waitloop")
        sb.AppendLine(")")
        sb.AppendLine("timeout /t 1 /nobreak >nul")
        sb.AppendLine("xcopy """ & srcRoot & "\*"" """ & Path.GetDirectoryName(target) & "\"" /E /Y /I /Q >nul")
        sb.AppendLine("start """" """ & target & """ " & args)
        sb.AppendLine("rmdir /S /Q """ & Path.GetDirectoryName(extractDir) & """ >nul 2>nul")
        sb.AppendLine("del /F /Q ""%~f0"" >nul 2>nul")
        File.WriteAllText(script, sb.ToString(), Encoding.Default)
        Return script
    End Function

    Private Sub StartDetached(exe As String, args As String)
        Try
            Dim psi As New ProcessStartInfo(exe, args) With {.UseShellExecute = True, .WorkingDirectory = Path.GetDirectoryName(exe)}
            Diagnostics.Process.Start(psi)
        Catch ex As Exception
            Throw New UpdateException("启动新版本失败。" & vbCrLf & ex.Message, ex)
        End Try
    End Sub

    ''' <summary>启动时调用：清理上次更新残留（*.old、暂存目录）。</summary>
    Public Sub CleanupAfterUpdate()
        Try
            Dim exe = ExePath
            Dim old = exe & ".old"
            If File.Exists(old) Then
                Try
                    File.Delete(old)
                Catch
                End Try
            End If
            If Directory.Exists(StagingRoot) Then
                For Each d In Directory.GetDirectories(StagingRoot)
                    Try
                        Directory.Delete(d, True)
                    Catch
                    End Try
                Next
            End If
        Catch
        End Try
    End Sub

    ''' <summary>判断本次启动是否由更新流程触发（PCL 原版约定：命令行含 --update）。</summary>
    Public Function IsUpdatedLaunch(args As String()) As Boolean
        If args Is Nothing Then Return False
        Return args.Any(Function(a) String.Equals(a, "--updated", StringComparison.OrdinalIgnoreCase))
    End Function

    ''' <summary>从命令行取出更新后的版本号（--update 之后第一个参数）。</summary>
    Public Function GetUpdatedVersion(args As String()) As String
        If args Is Nothing Then Return ""
        For i = 0 To args.Length - 2
            If String.Equals(args(i), "--updated", StringComparison.OrdinalIgnoreCase) Then Return args(i + 1).Trim(""""c)
        Next
        Return ""
    End Function

#End Region

#Region "工具"

    Private Function ResolveToken() As String
        If Not String.IsNullOrWhiteSpace(GitHubToken) Then Return GitHubToken.Trim()
        Dim env As String = Nothing
        Try
            env = Environment.GetEnvironmentVariable("PCL_UPDATE_TOKEN")
            If String.IsNullOrWhiteSpace(env) Then env = Environment.GetEnvironmentVariable("GITHUB_TOKEN")
        Catch
        End Try
        Return If(env, "").Trim()
    End Function

    Private Sub EnsureTls()
        Try
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 Or SecurityProtocolType.Tls13
        Catch
            Try
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12
            Catch
            End Try
        End Try
        Try
            ServicePointManager.Expect100Continue = False
        Catch
        End Try
    End Sub

    Private Function HttpGetString(url As String) As String
        EnsureTls()
        Dim req = CType(WebRequest.Create(url), HttpWebRequest)
        req.UserAgent = UserAgent
        Dim tok = ResolveToken()
        If tok.Length > 0 AndAlso url.IndexOf("api.github.com", StringComparison.OrdinalIgnoreCase) >= 0 Then
            req.Headers.Add("Authorization", "Bearer " & tok)
        End If
        req.Accept = "application/vnd.github+json, application/json"
        req.Timeout = RequestTimeout
        req.ReadWriteTimeout = RequestTimeout
        req.AutomaticDecompression = DecompressionMethods.GZip Or DecompressionMethods.Deflate
        Using resp = CType(req.GetResponse(), HttpWebResponse)
            Using rs = resp.GetResponseStream()
                Using sr As New StreamReader(rs, Encoding.UTF8)
                    Return sr.ReadToEnd()
                End Using
            End Using
        End Using
    End Function

    Private Function SafeName(name As String) As String
        If String.IsNullOrWhiteSpace(name) Then Return "update"
        For Each c In Path.GetInvalidFileNameChars()
            name = name.Replace(c, "_"c)
        Next
        Return name
    End Function

    Public Function FormatSize(bytes As Long) As String
        If bytes < 1024 Then Return bytes & " B"
        If bytes < 1024 * 1024 Then Return (bytes / 1024.0).ToString("0.0") & " KB"
        If bytes < 1024L * 1024 * 1024 Then Return (bytes / 1048576.0).ToString("0.00") & " MB"
        Return (bytes / 1073741824.0).ToString("0.00") & " GB"
    End Function

    ''' <summary>取 JSON 中某个键的字符串值（支持字符串/数字/布尔，自动反转义）。</summary>
    Public Function JsonValue(json As String, key As String) As String
        Dim m = Regex.Match(json, """" & Regex.Escape(key) & """\s*:\s*(?:""((?:[^""\\]|\\.)*)""|(-?\d+(?:\.\d+)?)|(true|false))",
                            RegexOptions.IgnoreCase)
        If Not m.Success Then Return ""
        If m.Groups(1).Success Then Return Unescape(m.Groups(1).Value)
        If m.Groups(2).Success Then Return m.Groups(2).Value
        Return m.Groups(3).Value
    End Function

    Private Function Unescape(s As String) As String
        If s.IndexOf("\"c) < 0 Then Return s
        Dim sb As New StringBuilder(s.Length)
        Dim i = 0
        While i < s.Length
            Dim c = s(i)
            If c = "\"c AndAlso i + 1 < s.Length Then
                i += 1
                Select Case s(i)
                    Case "n"c : sb.Append(vbLf)
                    Case "r"c : sb.Append(vbCr)
                    Case "t"c : sb.Append(vbTab)
                    Case "u"c
                        If i + 4 < s.Length Then
                            Dim code As Integer
                            If Integer.TryParse(s.Substring(i + 1, 4), Globalization.NumberStyles.HexNumber, Nothing, code) Then
                                sb.Append(ChrW(code))
                                i += 4
                            End If
                        End If
                    Case Else : sb.Append(s(i))
                End Select
                i += 1
            Else
                sb.Append(c)
                i += 1
            End If
        End While
        Return sb.ToString()
    End Function

    ''' <summary>取出 "key": [ ... ] 的数组区域（含两侧方括号）。</summary>
    Private Function JsonArrayRegion(json As String, key As String) As String
        Dim m = Regex.Match(json, """" & Regex.Escape(key) & """\s*:\s*\[")
        If Not m.Success Then Return Nothing
        Dim start = m.Index + m.Length - 1
        Dim depth = 0, inStr = False, esc = False
        For i = start To json.Length - 1
            Dim c = json(i)
            If inStr Then
                If esc Then
                    esc = False
                ElseIf c = "\"c Then
                    esc = True
                ElseIf c = """"c Then
                    inStr = False
                End If
            Else
                Select Case c
                    Case """"c : inStr = True
                    Case "["c : depth += 1
                    Case "]"c
                        depth -= 1
                        If depth = 0 Then Return json.Substring(start, i - start + 1)
                End Select
            End If
        Next
        Return Nothing
    End Function

    ''' <summary>把 [{...},{...}] 拆成单个对象字符串。</summary>
    Private Iterator Function SplitObjects(arr As String) As IEnumerable(Of String)
        Dim depth = 0, inStr = False, esc = False, objStart = -1
        For i = 0 To arr.Length - 1
            Dim c = arr(i)
            If inStr Then
                If esc Then
                    esc = False
                ElseIf c = "\"c Then
                    esc = True
                ElseIf c = """"c Then
                    inStr = False
                End If
            Else
                Select Case c
                    Case """"c : inStr = True
                    Case "{"c
                        depth += 1
                        If depth = 1 Then objStart = i
                    Case "}"c
                        depth -= 1
                        If depth = 0 AndAlso objStart >= 0 Then
                            Yield arr.Substring(objStart, i - objStart + 1)
                            objStart = -1
                        End If
                End Select
            End If
        Next
    End Function

#End Region

#Region "下载地址判断"

    ''' <summary>
    ''' 判断更新地址是否是可以直接下载文件的直链。
    ''' 网盘页面（如蓝奏云附带“密码: xxxx”的分享链接）返回 False，此时界面应改为“打开下载页”。
    ''' </summary>
    Public Function IsDirectDownloadLink(url As String) As Boolean
        If String.IsNullOrWhiteSpace(url) Then Return False
        Dim u = url.Trim().ToLowerInvariant()
        If Not u.StartsWith("http://") AndAlso Not u.StartsWith("https://") Then Return False
        If u.Contains(" ") OrElse u.Contains(",") OrElse u.Contains("密码") Then Return False
        Return u.Contains(".exe") OrElse u.Contains(".zip") OrElse u.Contains(".7z") OrElse
               u.Contains("/download") OrElse u.Contains("api.github.com") OrElse u.Contains("releases/download")
    End Function

#End Region

End Module


