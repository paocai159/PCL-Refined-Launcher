Imports System.Windows.Threading

Public Class PageLaunchRight
    Implements IRefreshable, IDispatcherUnhandledException

    ''' <summary>公告标题后的 NEW / 刷新按钮是否已经添加过（避免页面切换时重复添加）。</summary>
    Private _noticeInlineReady As Boolean = False
    ''' <summary>公告栏的刷新图标（用于旋转动画）。</summary>
    Private _refreshGlyph As Global.System.Windows.Controls.TextBlock = Nothing

    Private Sub Init() Handles Me.Loaded
        PanBack.ScrollToHome()
        PanLog.Visibility = ModeDebug.ToVisibility
        '公告栏：先显示加载中，拿到远程公告后再替换
        LabNoticeTitle.Text = ModNotice.NoticeTitle
        '标题整体下移、与正文左对齐，并放大让其更醒目
        Try
            PanNotice.MainTextBlock.Margin = New Thickness(25, 20, 23, 0)
            PanNotice.MainTextBlock.FontSize = 15.5
            'MyCard 默认把标题设为不可命中（IsHitTestVisible=False），这里要放开，否则标题里的刷新按钮点不动
            PanNotice.MainTextBlock.IsHitTestVisible = True
        Catch ex As Exception
            Logger.Warn(ex, "调整公告标题样式失败")
        End Try
        'NEW 标记与刷新按钮只添加一次：页面切换会再次触发 Loaded，否则每切换一次就多一个
        If Not _noticeInlineReady Then
            _noticeInlineReady = True
            If ModAnnouncement.ShowNew AndAlso PanNotice.Inlines.Count = 1 Then
                PanNotice.Inlines.Add(New Global.System.Windows.Documents.Run("  "))
                PanNotice.Inlines.Add(New Global.System.Windows.Documents.Run(" NEW ") With {
                    .Background = New Global.System.Windows.Media.SolidColorBrush(Global.System.Windows.Media.Color.FromRgb(&HFF, &HF1, &HE6)),
                    .Foreground = New Global.System.Windows.Media.SolidColorBrush(Global.System.Windows.Media.Color.FromRgb(&HFA, &H64, &H0)),
                    .FontSize = 12.5,
                    .FontWeight = Global.System.Windows.FontWeights.Bold,
                    .BaselineAlignment = Global.System.Windows.BaselineAlignment.Center})
            End If
            AddRefreshInline()
        End If
        '先显示加载中，等远程公告拿到后再替换内容
        LabNoticeTime.Text = ""
        LabNotice.Text = "公告加载中……"
        PanNotice.Visibility = Visibility.Visible
        FetchAnnouncementAsync()
        '快照版提示
        PanHint.Visibility =
            (BuildType <> BuildTypes.Release AndAlso VersionBranchMain = "Official" AndAlso
             CurrentRank < DonationRank.Rank23 AndAlso Not Settings.Get(Of Boolean)("HintSnapshot")).ToVisibility
    End Sub

#Region "公告"

    ''' <summary>在后台线程拉取远程公告，成功后回到 UI 线程刷新公告栏；失败显示错误提示。</summary>
    Private Sub FetchAnnouncementAsync()
        Global.System.Threading.Tasks.Task.Run(
            Sub()
                Try
                    Dim Item As ModAnnouncement.AnnounceItem = ModAnnouncement.Fetch()
                    RunInUi(Sub() ApplyAnnouncement(Item))
                Catch ex As Exception
                    Logger.Warn(ex, "获取远程公告失败")
                    RunInUi(Sub() ShowAnnouncementError())
                End Try
            End Sub)
    End Sub

    ''' <summary>把公告内容显示到公告栏。必须在 UI 线程调用。</summary>
    Private Sub ApplyAnnouncement(Item As ModAnnouncement.AnnounceItem)
        If Item Is Nothing Then Return
        StopRefreshSpin()
        LabNoticeTitle.Text = Item.Title
        Dim Text As String = Item.Content
        If Not String.IsNullOrWhiteSpace(Item.Link) Then
            Text &= vbCrLf & If(String.IsNullOrWhiteSpace(Item.LinkText), Item.Link, Item.LinkText & "：" & Item.Link)
        End If
        LabNotice.Text = Text
        LabNoticeTime.Text = Item.Time
        PanNotice.Visibility = Visibility.Visible
    End Sub

    ''' <summary>公告加载失败时的提示。必须在 UI 线程调用。</summary>
    Private Sub ShowAnnouncementError()
        StopRefreshSpin()
        LabNoticeTime.Text = ""
        LabNotice.Text = "公告加载失败，请检查网络连接或稍后再试。" & vbCrLf & "（点击标题后的刷新按钮可重新获取）"
        PanNotice.Visibility = Visibility.Visible
    End Sub

    ''' <summary>点刷新按钮：进入加载中状态、旋转图标并真正重新拉取公告。</summary>
    Private Sub ReloadAnnouncement()
        StartRefreshSpin()
        LabNoticeTime.Text = ""
        LabNotice.Text = "公告加载中……"
        FetchAnnouncementAsync()
    End Sub

    ''' <summary>让刷新图标转起来，作为正在刷新的视觉反馈。</summary>
    Private Sub StartRefreshSpin()
        If _refreshGlyph Is Nothing Then Return
        Try
            Dim Rt As Global.System.Windows.Media.RotateTransform = TryCast(_refreshGlyph.RenderTransform, Global.System.Windows.Media.RotateTransform)
            If Rt Is Nothing Then Return
            Dim Anim As New Global.System.Windows.Media.Animation.DoubleAnimation(0, 360,
                New Global.System.Windows.Duration(Global.System.TimeSpan.FromMilliseconds(700)))
            Anim.RepeatBehavior = Global.System.Windows.Media.Animation.RepeatBehavior.Forever
            Rt.BeginAnimation(Global.System.Windows.Media.RotateTransform.AngleProperty, Anim)
        Catch ex As Exception
            Logger.Warn(ex, "启动刷新动画失败")
        End Try
    End Sub

    ''' <summary>停止刷新图标的旋转。</summary>
    Private Sub StopRefreshSpin()
        If _refreshGlyph Is Nothing Then Return
        Try
            Dim Rt As Global.System.Windows.Media.RotateTransform = TryCast(_refreshGlyph.RenderTransform, Global.System.Windows.Media.RotateTransform)
            If Rt IsNot Nothing Then Rt.BeginAnimation(Global.System.Windows.Media.RotateTransform.AngleProperty, Nothing)
        Catch ex As Exception
            Logger.Warn(ex, "停止刷新动画失败")
        End Try
    End Sub

    ''' <summary>在「公告」标题行末尾追加一个刷新按钮（带悬停/按下反馈，点击后旋转）。</summary>
    Private Sub AddRefreshInline()
        Try
            '已经加过刷新按钮就不再重复添加
            For Each Inline As Global.System.Windows.Documents.Inline In PanNotice.Inlines
                If TypeOf Inline Is Global.System.Windows.Documents.InlineUIContainer Then Return
            Next
            Dim Glyph As New Global.System.Windows.Controls.TextBlock With {
                .Text = ChrW(&HE72C),
                .FontFamily = New Global.System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                .FontSize = 12,
                .VerticalAlignment = Global.System.Windows.VerticalAlignment.Center,
                .RenderTransformOrigin = New Global.System.Windows.Point(0.5, 0.5)
            }
            Glyph.RenderTransform = New Global.System.Windows.Media.RotateTransform(0)
            Dim Btn As New Global.System.Windows.Controls.Button With {
                .Content = Glyph,
                .Background = Global.System.Windows.Media.Brushes.Transparent,
                .BorderThickness = New Global.System.Windows.Thickness(0),
                .Padding = New Global.System.Windows.Thickness(5, 2, 5, 2),
                .Margin = New Global.System.Windows.Thickness(3, 0, 0, 0),
                .Cursor = Global.System.Windows.Input.Cursors.Hand,
                .Focusable = False,
                .ToolTip = "刷新公告",
                .VerticalAlignment = Global.System.Windows.VerticalAlignment.Center,
                .Template = CreateRefreshButtonTemplate()
            }
            AddHandler Btn.Click, Sub(sender As Object, ev As Global.System.Windows.RoutedEventArgs) ReloadAnnouncement()
            _refreshGlyph = Glyph
            PanNotice.Inlines.Add(New Global.System.Windows.Documents.InlineUIContainer(Btn) With {
                .BaselineAlignment = Global.System.Windows.BaselineAlignment.Center})
        Catch ex As Exception
            Logger.Warn(ex, "添加公告刷新按钮失败")
        End Try
    End Sub

    ''' <summary>刷新按钮的样式模板：悬停浅蓝底、按下更深的蓝底，带圆角。</summary>
    Private Function CreateRefreshButtonTemplate() As Global.System.Windows.Controls.ControlTemplate
        Dim Tmpl As New Global.System.Windows.Controls.ControlTemplate(GetType(Global.System.Windows.Controls.Button))
        Dim Bd As New Global.System.Windows.FrameworkElementFactory(GetType(Global.System.Windows.Controls.Border))
        Bd.Name = "bdRefresh"
        Bd.SetValue(Global.System.Windows.Controls.Border.CornerRadiusProperty, New Global.System.Windows.CornerRadius(5))
        Bd.SetValue(Global.System.Windows.Controls.Border.BackgroundProperty,
            New Global.System.Windows.TemplateBindingExtension(Global.System.Windows.Controls.Control.BackgroundProperty))
        Dim Pst As New Global.System.Windows.FrameworkElementFactory(GetType(Global.System.Windows.Controls.ContentPresenter))
        Pst.SetValue(Global.System.Windows.Controls.ContentPresenter.HorizontalAlignmentProperty, Global.System.Windows.HorizontalAlignment.Center)
        Pst.SetValue(Global.System.Windows.Controls.ContentPresenter.VerticalAlignmentProperty, Global.System.Windows.VerticalAlignment.Center)
        Bd.AppendChild(Pst)
        Tmpl.VisualTree = Bd
        Dim TgHover As New Global.System.Windows.Trigger
        TgHover.Property = Global.System.Windows.Controls.Button.IsMouseOverProperty
        TgHover.Value = True
        TgHover.Setters.Add(New Global.System.Windows.Setter(Global.System.Windows.Controls.Border.BackgroundProperty,
            New Global.System.Windows.Media.SolidColorBrush(Global.System.Windows.Media.Color.FromRgb(&HDC, &HEB, &HFC)), "bdRefresh"))
        Tmpl.Triggers.Add(TgHover)
        Dim TgPressed As New Global.System.Windows.Trigger
        TgPressed.Property = Global.System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty
        TgPressed.Value = True
        TgPressed.Setters.Add(New Global.System.Windows.Setter(Global.System.Windows.Controls.Border.BackgroundProperty,
            New Global.System.Windows.Media.SolidColorBrush(Global.System.Windows.Media.Color.FromRgb(&HC6, &HDD, &HFA)), "bdRefresh"))
        Tmpl.Triggers.Add(TgPressed)
        Return Tmpl
    End Function

#End Region

#Region "主页"

    ''' <summary>
    ''' 刷新主页。
    ''' </summary>
    Private Sub Refresh() Handles Me.Loaded
        RunInThread(
        Sub()
            Try
                SyncLock RefreshLock
                    RefreshReal()
                End SyncLock
            Catch ex As Exception
                Logger.Error(ex, "加载 PCL 主页自定义信息失败", If(ModeDebug, LogBehavior.Alert, LogBehavior.Toast))
            End Try
        End Sub)
    End Sub
    Private Sub RefreshReal()
        Dim Content As String = Nothing, Url As String = Nothing
        Select Case Settings.Get(Of Integer)("UiCustomType")
            Case 1
                '加载本地文件
                Logger.Info("主页自定义数据来源：本地文件")
                Content = FileUtils.TryReadAsString(Paths.Base & "PCL\Custom.xaml")
            Case 2
                '联网下载
                Url = Settings.Get(Of String)("UiCustomNet")
            Case 3
                '预设
                Select Case Settings.Get(Of Integer)("UiCustomPreset")
                    Case 0
                        Logger.Info("主页预设：你知道吗")
                        Content = "
                            <local:MyCard Title=""你知道吗？"" Margin=""0,0,0,15"">
                                <TextBlock Margin=""25,38,23,15"" FontSize=""13.5"" IsHitTestVisible=""False"" Text=""{hint}"" TextWrapping=""Wrap"" Foreground=""{DynamicResource ColorBrush1}"" />
                                <local:MyIconButton Height=""22"" Width=""22"" Margin=""9"" VerticalAlignment=""Top"" HorizontalAlignment=""Right"" 
                                    EventType=""刷新主页"" EventData=""/""
                                    Logo=""M512.0 838.3c-80.2 0-153.4-29.3-210.2-77.4l75.5-75.5c11.5-11.5 25.8-22.0 25.8-37.0a27.2 27.2 0 0 0-27.1-27.1H104.0c-27.1 0-27.1 23.9-27.1 27.1v271.9a27.1 27.1 0 0 0 27.1 27.1c15.0 0 27.8-16.6 42.5-31.2l77.9-77.9c76.6 67.7 177.1 108.9 287.4 108.9 221.7 0 404.5-166.0 431.2-380.6h-109.8c-25.9 154.2-159.7 271.9-321.3 271.9zM919.9 76.6c-15.0 0-27.8 16.6-42.5 31.3L799.5 185.8c-76.5-67.7-177.1-108.9-287.4-108.9-221.8 0-404.5 166.1-431.3 380.6H190.6c25.9-154.2 159.7-271.9 321.4-271.9 80.2 0 153.4 29.3 210.1 77.4l-75.5 75.5c-11.6 11.5-25.8 22.0-25.8 37.1a27.2 27.2 0 0 0 27.1 27.1h271.9c27.1 0 27.1-23.9 27.1-27.1V103.8a27.1 27.1 0 0 0-27.1-27.1z"" />
                            </local:MyCard>"
                    Case 1
                        Logger.Info("主页预设：回声洞")
                        Content = "
                            <local:MyCard Title=""回声洞"" Margin=""0,0,0,15"">
                                <TextBlock Margin=""25,38,23,15"" FontSize=""13.5"" IsHitTestVisible=""False"" Text=""{cave}"" TextWrapping=""Wrap"" Foreground=""{DynamicResource ColorBrush1}"" />
                                <local:MyIconButton Height=""22"" Width=""22"" Margin=""9"" VerticalAlignment=""Top"" HorizontalAlignment=""Right"" 
                                    EventType=""刷新主页"" EventData=""/""
                                    Logo=""M512.0 838.3c-80.2 0-153.4-29.3-210.2-77.4l75.5-75.5c11.5-11.5 25.8-22.0 25.8-37.0a27.2 27.2 0 0 0-27.1-27.1H104.0c-27.1 0-27.1 23.9-27.1 27.1v271.9a27.1 27.1 0 0 0 27.1 27.1c15.0 0 27.8-16.6 42.5-31.2l77.9-77.9c76.6 67.7 177.1 108.9 287.4 108.9 221.7 0 404.5-166.0 431.2-380.6h-109.8c-25.9 154.2-159.7 271.9-321.3 271.9zM919.9 76.6c-15.0 0-27.8 16.6-42.5 31.3L799.5 185.8c-76.5-67.7-177.1-108.9-287.4-108.9-221.8 0-404.5 166.1-431.3 380.6H190.6c25.9-154.2 159.7-271.9 321.4-271.9 80.2 0 153.4 29.3 210.1 77.4l-75.5 75.5c-11.6 11.5-25.8 22.0-25.8 37.1a27.2 27.2 0 0 0 27.1 27.1h271.9c27.1 0 27.1-23.9 27.1-27.1V103.8a27.1 27.1 0 0 0-27.1-27.1z"" />
                            </local:MyCard>"
                    Case 2
                        Logger.Info("主页预设：Minecraft 新闻")
                        Url = "https://mcnews.meloong.com"
                    Case 3
                        Logger.Info("主页预设：简单主页")
                        Url = "https://pclhomeplazaoss.lingyunawa.top:26994/d/Homepages/MFn233/Custom.xaml"
                    Case 4
                        Logger.Info("主页预设：每日整合包推荐")
                        Url = "https://pclsub.sodamc.com/"
                    Case 5
                        Logger.Info("主页预设：Minecraft 皮肤推荐")
                        Url = "https://forgepixel.com/pcl_sub_file"
                    Case 6
                        Logger.Info("主页预设：OpenBMCLAPI 仪表盘 Lite")
                        Url = "https://pcl-bmcl.milu.ink/"
                    Case 7
                        Logger.Info("主页预设：主页市场")
                        Url = "https://pclhomeplazaoss.lingyunawa.top:26994/p/Homepages/Homepage.Market/Custom.xaml"
                    Case 8
                        Logger.Info("主页预设：更新日志")
                        Url = "https://pclhomeplazaoss.lingyunawa.top:26994/d/Homepages/Joker2184/UpdateHomepage.xaml"
                    Case 9
                        Logger.Info("主页预设：PCL 新功能说明书")
                        Url = "https://raw.gitcode.com/WForst-Breeze/WhatsNewPCL/raw/main/Custom.xaml"
                    Case 10
                        Logger.Info("主页预设：OpenMCIM Dashboard")
                        Url = "https://files.mcimirror.top/PCL"
                    Case 11
                        Logger.Info("主页预设：杂志主页")
                        Url = "https://gh-proxy.com/https://github.com/Neclyon/Magazine-Homepage-PCL/raw/main/output/Custom.xaml"
                    Case 12
                        Logger.Info("主页预设：PCL GitHub 仪表盘")
                        Url = "https://ddf.pcl-community.top/Custom.xaml"
                    Case 13
                        Logger.Info("主页预设：PCL 更新摘要")
                        Url = "https://raw.gitcode.com/ENC_Euphony/PCL-AI-Summary-HomePage/raw/master/Custom.xaml"
                    Case 14
                        Logger.Info("主页预设：今日新闻热点")
                        Url = "https://pcl.wyc-w.top/index.xaml"
                    Case 15
                        Logger.Info("主页预设：Minecraft 芝士站")
                        Url = "https://mkss-services.awaeric.xyz"
                    Case 16
                        Logger.Info("主页预设：整合包推荐引擎")
                        Url = "https://qawsedrftgyhujiko.fun/pcl2/Custom.xaml"
                    Case 17
                        Logger.Info("主页预设：Bangumi 番剧主页")
                        Url = "https://bangumi.p.kaphia.top"
                    Case 18
                        Logger.Info("主页预设：Bilibili 热门")
                        Url = "https://bilibili.p.kaphia.top"
                    Case 19
                        Logger.Info("主页预设：Music 云音乐热门")
                        Url = "https://cloudmusic.p.kaphia.top"
                    Case 20
                        Logger.Info("主页预设：模组推荐")
                        Url = "https://pcl.xn--9iq209asnop94b.top/download/ModPage.xaml"
                    Case 21
                        Logger.Info("主页预设：每日游戏推荐")
                        Url = "https://pcl.xn--9iq209asnop94b.top/download/GamePage.xaml"
                    Case 22
                        Logger.Info("主页预设：Wallpaper 每日壁纸")
                        Url = "https://wallpaper.p.kaphia.top"
                    Case 23
                        Logger.Info("主页预设：Weather 天气预报")
                        Url = "https://weather-p.kaphia.top/{variable:PCLDailyWeatherPage:choose}.xaml"
                    Case 24
                        Logger.Info("主页预设：新一代狗屁王大杂烩主页")
                        Url = "https://end.g-fish.dpdns.org/Custom.xaml"
                End Select
        End Select
        '联网下载
        If Not String.IsNullOrWhiteSpace(Url) Then
            '替换自定义变量与设置
            Url = ArgumentReplace(Url, AddressOf WebUtility.HtmlEncode)
            If Url = Settings.Get(Of String)("CacheSavedPageUrl") AndAlso FileUtils.Exists(PathTemp & "Cache\Custom.xaml") Then
                '缓存可用
                Logger.Info("主页自定义数据来源：联网缓存文件")
                Content = FileUtils.TryReadAsString(PathTemp & "Cache\Custom.xaml")
                '后台更新缓存
                OnlineLoader.Start((Url, False))
            Else
                '缓存不可用
                Logger.Info("主页自定义数据来源：联网全新下载")
                Hint("正在加载主页……")
                RunInUiWait(Sub() LoadContent(Nothing)) '在加载结束前清空页面
                Settings.Set("CacheSavedPageVersion", "")
                OnlineLoader.Start((Url, True)) '下载完成后将会再次触发更新
                Return
            End If
        End If
        '同步到 UI
        RunInUi(Sub() LoadContent(Content))
    End Sub
    Private RefreshLock As New Object

    '联网获取主页文件
    Private OnlineLoader As New LoaderTask(Of (Address As String, ShouldRefresh As Boolean), Integer)("下载主页", AddressOf OnlineLoaderSub) With {.ReloadTimeout = 10 * 60 * 1000}
    Private Sub OnlineLoaderSub(Task As LoaderTask(Of (Address As String, ShouldRefresh As Boolean), Integer))
        Dim Address As String = Task.Input.Address '#3721 中连续触发两次导致内容变化
        Dim ShouldRefresh As Boolean = Task.Input.ShouldRefresh
        Try
            '获取版本校验地址
            Dim VersionAddress As String
            If Address.Contains(".xaml") Then
                VersionAddress = Address.Replace(".xaml", ".xaml.ini")
            Else
                VersionAddress = Address.BeforeFirst("?")
                If Not VersionAddress.EndsWithF("/") Then VersionAddress += "/"
                VersionAddress += "version"
                If Address.Contains("?") Then VersionAddress += "?" & Address.AfterFirst("?")
            End If
            '校验版本
            Dim Version As String = ""
            Try
                Version = NetRequestByClientRetry(VersionAddress)
                If Version.Length > 1000 Then Throw New Exception($"获取的主页版本过长（{Version.Length} 字符）")
                Dim CurrentVersion As String = Settings.Get(Of String)("CacheSavedPageVersion")
                If Version <> "" AndAlso CurrentVersion <> "" AndAlso Version = CurrentVersion Then
                    Logger.Info($"当前缓存的主页已为最新，当前版本：{Version}，检查源：{VersionAddress}")
                    Return
                End If
                Logger.Info($"需要下载联网主页，当前版本：{Version}，检查源：{VersionAddress}")
            Catch exx As Exception
                Logger.Warn(exx, $"联网获取主页版本失败")
                Logger.Info($"无法检查联网主页版本，将直接下载，检查源：{VersionAddress}")
            End Try
            '实际下载
            Dim FileContent As String = NetRequestByClientRetry(Address)
            Logger.Info($"已联网下载主页，内容长度：{FileContent.Length}，来源：{Address}")
            Settings.Set("CacheSavedPageUrl", Address)
            Settings.Set("CacheSavedPageVersion", Version)
            FileUtils.Write(PathTemp & "Cache\Custom.xaml", FileContent)
            '若内容变更则要求刷新
            If LoadedContentHash <> FileContent.GetStableHashCode() AndAlso ShouldRefresh Then Refresh()
        Catch ex As Exception
            Logger.Error(ex, $"下载主页失败（{Address}）", If(ModeDebug, LogBehavior.Alert, LogBehavior.Toast))
        End Try
    End Sub

    ''' <summary>
    ''' 立即强制刷新主页。
    ''' 必须在 UI 线程调用。
    ''' </summary>
    Public Sub ForceRefresh() Implements IRefreshable.Refresh
        Logger.Info("要求强制刷新主页")
        ClearCache()
        '实际的刷新
        If FrmMain.PageCurrent.Page = FormMain.PageType.Launch Then
            PanBack.ScrollToHome()
            Refresh()
        Else
            FrmMain.PageChange(FormMain.PageType.Launch)
        End If
    End Sub

    ''' <summary>
    ''' 清空主页缓存信息。
    ''' </summary>
    Private Sub ClearCache()
        LoadedContentHash = Nothing
        OnlineLoader.Input = ("", True)
        Settings.Set("CacheSavedPageUrl", "")
        Settings.Set("CacheSavedPageVersion", "")
        Logger.Info("已清空主页缓存")
    End Sub

    ''' <summary>
    ''' 从文本内容中加载主页。
    ''' 必须在 UI 线程调用。
    ''' </summary>
    Private Sub LoadContent(Content As String)
        Try
            SyncLock LoadContentLock
                '如果加载目标内容一致则不加载
                Dim Hash = If(Content, "").GetStableHashCode()
                If Hash = LoadedContentHash Then Return
                LoadedContentHash = Hash
                '实际加载内容
                PanCustom.Children.Clear()
                If String.IsNullOrWhiteSpace(Content) Then
                    Logger.Info($"实例化：清空主页 UI，来源为空")
                    Return
                End If
                Dim LoadStartTime As Date = Date.Now
                '修改时应同时修改 PageOtherHelpDetail.Init
                Content = ArgumentReplace(Content, AddressOf StringUtils.XmlEscape)
                Do While Content.Contains("xmlns")
                    Content = Content.RegexReplace("xmlns[^""']*(""|')[^""']*(""|')", "").Replace("xmlns", "") '禁止声明命名空间
                Loop
                Content = "<StackPanel xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"" xmlns:sys=""clr-namespace:System;assembly=mscorlib"" xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml"" xmlns:local=""clr-namespace:PCL;assembly=Plain Craft Launcher 2"" xmlns:core=""clr-namespace:MeloongCore;assembly=MeloongCore"" xmlns:corewpf=""clr-namespace:MeloongCore.Wpf;assembly=MeloongCore.Wpf"">" & Content & "</StackPanel>"
                Logger.Info($"实例化：加载主页 UI 开始，最终内容长度：{Content.Count}")
                PanCustom.Children.Add(GetObjectFromXML(Content))
                '加载计时
                Dim LoadCostTime = (Date.Now - LoadStartTime).Milliseconds
                Logger.Info($"实例化：加载主页 UI 完成，耗时 {LoadCostTime}ms")
                If LoadCostTime > 3000 Then Hint($"主页加载过于缓慢（花费了 {Math.Round(LoadCostTime / 1000, 1)} 秒），请向主页作者反馈此问题，或暂时停止使用该主页")
            End SyncLock
        Catch ex As Exception
            Logger.Warn(ex, $"加载失败的主页内容：{vbCrLf}{Content}")
            OnLoadContentFailed(ex)
        End Try
    End Sub
    ''' <summary>
    ''' 加载主页失败时调用。
    ''' </summary>
    Private Sub OnLoadContentFailed(ex As Exception)
        If ModeDebug OrElse Settings.Get(Of Integer)("UiCustomType") = 1 Then
            Logger.Warn(ex, "加载主页失败")
            If MyMsgBox(If(TypeOf ex Is UnauthorizedAccessException, ex.Message, $"主页内容编写有误，请根据下列错误信息进行检查：{vbCrLf}{ex.GetDisplay(False)}"),
                        "加载主页失败", "重试", "取消") = 1 Then ForceRefresh()
        Else
            Logger.Error(ex, "加载主页失败", LogBehavior.Toast)
        End If
    End Sub
    ''' <summary>
    ''' 捕获主页在 Measure 和 Arrange 阶段抛出的异常。
    ''' </summary>
    Private Sub DispatcherUnhandledException(sender As Object, e As DispatcherUnhandledExceptionEventArgs) Implements IDispatcherUnhandledException.DispatcherUnhandledException
        If TypeOf e.Exception IsNot Markup.XamlParseException Then Return
        e.Handled = True
        LoadContent(Nothing)
        OnLoadContentFailed(e.Exception)
    End Sub

    Private LoadedContentHash As ULong? = Nothing
    Private LoadContentLock As New Object

#End Region

End Class
