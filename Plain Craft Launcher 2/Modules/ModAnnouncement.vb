Imports Newtonsoft.Json.Linq

''' <summary>
''' 启动器公告的数据结构与远程拉取。
''' 公告内容来自 announce.json，失败时回落到本地缓存，再失败则使用 ModNotice 里的内置公告。
''' </summary>
Public Module ModAnnouncement

    ''' <summary>公告数据地址。</summary>
    Public Const AnnounceUrl As String = "https://pcl-refined-launcher-announcement.pages.dev/announce.json"

    ''' <summary>公告数据的 Magic 校验值，防止拿到错误的 JSON。</summary>
    Public Const AnnounceMagic As String = "##PCLREFINED##"

    ''' <summary>本地缓存文件名。</summary>
    Public Const CacheFileName As String = "AnnounceCache.json"

    ''' <summary>本次启动是否显示公告栏的橙色 NEW（只在更新完成或首次打开时为 True）。</summary>
    Public ShowNew As Boolean = False

    ''' <summary>一条公告。</summary>
    Public Class AnnounceItem
        Public Title As String = ""
        Public Content As String = ""
        Public Time As String = ""
        Public Level As String = "info"
        Public Link As String = ""
        Public LinkText As String = ""
    End Class

    ''' <summary>本地缓存的完整路径。</summary>
    Public ReadOnly Property CachePath As String
        Get
            Return Paths.BaseThenName & CacheFileName
        End Get
    End Property

    ''' <summary>
    ''' 联网拉取公告（会阻塞，请在线程中调用）。
    ''' </summary>
    Public Function Fetch() As AnnounceItem
        '带上时间戳，避免 CDN 或本地缓存返回旧公告，保证每次都是真正重新拉取
        Dim Url As String = AnnounceUrl & If(AnnounceUrl.Contains("?"), "&", "?") & "t=" & DateTime.Now.Ticks.ToString()
        Dim Raw As String = NetRequestByClientRetry(Url, RequireJson:=True)
        Dim Result As AnnounceItem = Parse(Raw)
        '顺带写入缓存，下次断网时仍能显示
        Try
            FileUtils.Write(CachePath, Raw)
        Catch ex As Exception
            Logger.Warn(ex, "写入公告缓存失败")
        End Try
        Return Result
    End Function

    ''' <summary>
    ''' 解析 announce.json。
    ''' </summary>
    Public Function Parse(Json As String) As AnnounceItem
        Dim Obj As JObject = JObject.Parse(Json)
        Dim MagicValue As String = Obj.Value(Of String)("Magic")
        If MagicValue <> AnnounceMagic Then Throw New FormatException($"公告数据校验失败（Magic = {If(MagicValue, "null")}）")
        Dim Result As New AnnounceItem With {
            .Title = If(Obj.Value(Of String)("Title"), "").Trim,
            .Content = If(Obj.Value(Of String)("Content"), "").Trim,
            .Time = If(Obj.Value(Of String)("Time"), "").Trim,
            .Level = If(Obj.Value(Of String)("Level"), "info").Trim.ToLowerInvariant,
            .Link = If(Obj.Value(Of String)("Link"), "").Trim,
            .LinkText = If(Obj.Value(Of String)("LinkText"), "").Trim
        }
        If String.IsNullOrWhiteSpace(Result.Title) Then Result.Title = "启动器公告"
        If String.IsNullOrWhiteSpace(Result.Content) Then Throw New FormatException("公告内容为空")
        Return Result
    End Function

    ''' <summary>
    ''' 读取本地缓存的公告；没有缓存或解析失败时返回 Nothing。
    ''' </summary>
    Public Function TryLoadCache() As AnnounceItem
        Try
            If Not IO.File.Exists(CachePath) Then Return Nothing
            Return Parse(FileUtils.TryReadAsString(CachePath))
        Catch ex As Exception
            Logger.Warn(ex, "读取公告缓存失败")
            Return Nothing
        End Try
    End Function

End Module
