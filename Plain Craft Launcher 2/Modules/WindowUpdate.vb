Option Strict On
Option Explicit On

' ============================================================================
'  PCL 启动器更新窗口（WindowUpdate）
'  纯代码 WPF 窗口，不需要 XAML；与 Modules\ModUpdate.vb 配套使用。
' ============================================================================

Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media
Imports System.Windows.Shapes
Imports System.Windows.Threading

''' <summary>启动器更新窗口：检查新版本、展示更新内容、下载并应用更新。</summary>
Friend Class WindowUpdate
    Inherits Window

    '---- 配色 ----
    Private Shared ReadOnly Accent As Color = Color.FromRgb(&H0F, &H86, &HE8)
    Private Shared ReadOnly AccentBrush As New SolidColorBrush(Color.FromRgb(&H0F, &H86, &HE8))
    Private Shared ReadOnly TextMain As New SolidColorBrush(Color.FromRgb(&H1F, &H23, &H28))
    Private Shared ReadOnly TextSub As New SolidColorBrush(Color.FromRgb(&H75, &H7B, &H84))
    Private Shared ReadOnly CardBorder As New SolidColorBrush(Color.FromRgb(&HE3, &HE6, &HEB))
    Private Shared ReadOnly BgLight As New SolidColorBrush(Color.FromRgb(&HF7, &HF8, &HFA))

    Private _info As UpdateInfo
    Private _cts As CancellationTokenSource
    Private _busy As Boolean
    Private _checked As Boolean

    Private ReadOnly _lblVersion As TextBlock
    Private ReadOnly _txtLog As TextBox
    Private ReadOnly _bar As ProgressBar
    Private ReadOnly _lblStage As TextBlock
    Private ReadOnly _btnUpdate As Button
    Private ReadOnly _btnLater As Button

    ''' <summary>操作进程：检查 → 下载 → 校验 → 解压替换 → 重启。</summary>
    Private ReadOnly _stepPanel As StackPanel
    Private ReadOnly _stepLabels As New List(Of TextBlock)
    Private _currentStep As Integer = -1
    Private Shared ReadOnly StepNames As String() = {"检查更新", "下载更新包", "校验完整性", "解压并替换", "重启启动器"}

    ''' <summary>可选日志回调（接入 PCL 时指向 Logger.Info，方便排查）。</summary>
    Public Shared LogAction As Action(Of String)

    Private Shared Sub Log(msg As String)
        Try
            If LogAction IsNot Nothing Then LogAction(msg)
        Catch
        End Try
    End Sub

    ''' <summary>当前打开着的更新窗口（用于防止重复弹窗）。</summary>
    Private Shared _current As WindowUpdate

#Region "构造与界面"

    ''' <summary>用已知的更新信息打开窗口；info 为 Nothing 时自动检查更新。</summary>
    Public Sub New(info As UpdateInfo)
        _info = info

        Title = "PCL 更新"
        Width = 560
        Height = 440
        MinWidth = 520
        MinHeight = 380
        WindowStartupLocation = WindowStartupLocation.CenterScreen
        ResizeMode = ResizeMode.NoResize
        Background = BgLight
        FontFamily = New FontFamily("Microsoft YaHei UI, Segoe UI")
        FontSize = 13

        Dim root As New Grid()
        root.RowDefinitions.Add(New RowDefinition With {.Height = GridLength.Auto})
        root.RowDefinitions.Add(New RowDefinition With {.Height = GridLength.Auto})
        root.RowDefinitions.Add(New RowDefinition With {.Height = New GridLength(1, GridUnitType.Star)})
        root.RowDefinitions.Add(New RowDefinition With {.Height = GridLength.Auto})
        root.RowDefinitions.Add(New RowDefinition With {.Height = GridLength.Auto})

        '---- 顶部：标题 ----
        Dim header As New Grid With {.Margin = New Thickness(24, 20, 24, 0)}
        header.ColumnDefinitions.Add(New ColumnDefinition With {.Width = GridLength.Auto})
        header.ColumnDefinitions.Add(New ColumnDefinition With {.Width = New GridLength(1, GridUnitType.Star)})

        Dim mark As New Rectangle With {
            .Width = 4,
            .Height = 22,
            .RadiusX = 2,
            .RadiusY = 2,
            .Fill = AccentBrush,
            .VerticalAlignment = VerticalAlignment.Center,
            .Margin = New Thickness(0, 0, 10, 0)
        }
        Grid.SetColumn(mark, 0)
        header.Children.Add(mark)

        Dim titlePanel As New StackPanel With {.Orientation = Orientation.Horizontal, .VerticalAlignment = VerticalAlignment.Center}
        titlePanel.Children.Add(New TextBlock With {
            .Text = "启动器更新",
            .FontSize = 20,
            .FontWeight = FontWeights.SemiBold,
            .Foreground = TextMain
        })
        Grid.SetColumn(titlePanel, 1)
        header.Children.Add(titlePanel)
        Grid.SetRow(header, 0)
        root.Children.Add(header)

        '---- 版本条 ----
        Dim versionRow As New StackPanel With {.Margin = New Thickness(24, 14, 24, 0)}
        Dim chip As New Border With {
            .CornerRadius = New CornerRadius(6),
            .Background = New SolidColorBrush(Color.FromRgb(&HE9, &HF2, &HFE)),
            .Padding = New Thickness(10, 5, 10, 5),
            .HorizontalAlignment = HorizontalAlignment.Left
        }
        _lblVersion = New TextBlock With {
            .Text = "当前版本 " & ModUpdate.CurrentVersion & "，正在检查更新…",
            .Foreground = AccentBrush,
            .FontSize = 12.5,
            .FontWeight = FontWeights.SemiBold
        }
        chip.Child = _lblVersion
        versionRow.Children.Add(chip)
        Grid.SetRow(versionRow, 1)
        root.Children.Add(versionRow)

        '---- 更新内容卡片 ----
        Dim card As New Border With {
            .CornerRadius = New CornerRadius(8),
            .Background = Brushes.White,
            .BorderBrush = CardBorder,
            .BorderThickness = New Thickness(1),
            .Margin = New Thickness(24, 12, 24, 0),
            .Padding = New Thickness(14, 12, 8, 12)
        }
        _txtLog = New TextBox With {
            .Text = "",
            .IsReadOnly = True,
            .TextWrapping = TextWrapping.Wrap,
            .VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            .AcceptsReturn = True,
            .BorderThickness = New Thickness(0),
            .Background = Brushes.Transparent,
            .Foreground = TextMain,
            .Padding = New Thickness(0),
            .FontFamily = New FontFamily("Microsoft YaHei UI, Consolas"),
            .FontSize = 13,
            .IsTabStop = False,
            .Visibility = Visibility.Collapsed
        }
        Dim scroller As New ScrollViewer With {
            .VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            .HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            .Content = _txtLog
        }
        card.Child = scroller
        Grid.SetRow(card, 2)
        root.Children.Add(card)

        '---- 操作进程（更新开始后显示） ----
        _stepPanel = New StackPanel With {
            .Margin = New Thickness(24, 12, 24, 0),
            .Visibility = Visibility.Collapsed
        }
        For Each stepName In StepNames
            Dim lbl As New TextBlock With {
                .Text = "○ " & stepName,
                .FontSize = 12.5,
                .Margin = New Thickness(0, 2, 0, 2),
                .Foreground = TextSub
            }
            _stepLabels.Add(lbl)
            _stepPanel.Children.Add(lbl)
        Next
        Grid.SetRow(_stepPanel, 3)
        root.Children.Add(_stepPanel)

        '---- 底部：进度 + 按钮 ----
        Dim bottom As New StackPanel With {.Margin = New Thickness(24, 0, 24, 20)}
        _bar = New ProgressBar With {
            .Height = 6,
            .Minimum = 0,
            .Maximum = 100,
            .Value = 0,
            .Margin = New Thickness(0, 16, 0, 0),
            .Visibility = Visibility.Collapsed,
            .Foreground = AccentBrush,
            .Background = New SolidColorBrush(Color.FromRgb(&HE4, &HE7, &HEC)),
            .BorderThickness = New Thickness(0),
            .Template = RoundedProgressTemplate()
        }
        _lblStage = New TextBlock With {
            .Text = "",
            .Margin = New Thickness(0, 7, 0, 0),
            .Foreground = TextSub,
            .FontSize = 12,
            .Visibility = Visibility.Collapsed,
            .TextTrimming = TextTrimming.CharacterEllipsis
        }
        bottom.Children.Add(_bar)
        bottom.Children.Add(_lblStage)

        Dim buttons As New StackPanel With {
            .Orientation = Orientation.Horizontal,
            .HorizontalAlignment = HorizontalAlignment.Right,
            .Margin = New Thickness(0, 18, 0, 0)
        }
        _btnLater = New Button With {
            .Content = "稍后再说",
            .MinWidth = 100,
            .Height = 34,
            .Margin = New Thickness(0, 0, 10, 0),
            .IsCancel = True,
            .Background = New SolidColorBrush(Color.FromRgb(&HED, &HF0, &HF4)),
            .Foreground = New SolidColorBrush(Color.FromRgb(&H3A, &H40, &H48)),
            .BorderThickness = New Thickness(0),
            .FontSize = 13,
            .Template = RoundedButtonTemplate()
        }
        _btnUpdate = New Button With {
            .Content = "立即更新",
            .MinWidth = 108,
            .Height = 34,
            .IsDefault = True,
            .IsEnabled = False,
            .Foreground = Brushes.White,
            .Background = AccentBrush,
            .BorderThickness = New Thickness(0),
            .FontSize = 13,
            .FontWeight = FontWeights.SemiBold,
            .Template = RoundedButtonTemplate()
        }
        AddHandler _btnLater.Click, Sub()
                                          Log("点击了稍后再说")
                                          Close()
                                      End Sub
        AddHandler _btnUpdate.Click, AddressOf OnUpdateClick
        buttons.Children.Add(_btnLater)
        buttons.Children.Add(_btnUpdate)
        bottom.Children.Add(buttons)

        Grid.SetRow(bottom, 4)
        root.Children.Add(bottom)

        Content = root

        If _info IsNot Nothing Then ShowInfo(_info) Else _btnUpdate.IsEnabled = True
        AddHandler Loaded, AddressOf OnLoaded
        AddHandler Closing, AddressOf WindowClosingHandler
        AddHandler Closed, Sub() Log("窗口已关闭（Closed 事件）")
    End Sub

    ''' <summary>圆角按钮模板（纯色背景 + 白字），避免默认 WPF 按钮的灰色渐变。</summary>
    Private Shared Function RoundedButtonTemplate() As ControlTemplate
        Dim bd As New FrameworkElementFactory(GetType(Border))
        bd.SetValue(Border.CornerRadiusProperty, New CornerRadius(6))
        bd.SetValue(Border.BackgroundProperty, New TemplateBindingExtension(Control.BackgroundProperty))
        bd.SetValue(Border.BorderThicknessProperty, New TemplateBindingExtension(Control.BorderThicknessProperty))
        bd.SetValue(Border.BorderBrushProperty, New TemplateBindingExtension(Control.BorderBrushProperty))

        Dim pst As New FrameworkElementFactory(GetType(ContentPresenter))
        pst.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center)
        pst.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center)
        pst.SetValue(FrameworkElement.MarginProperty, New Thickness(14, 0, 14, 0))
        bd.AppendChild(pst)

        Dim template As New ControlTemplate(GetType(Button))
        template.VisualTree = bd
        Return template
    End Function

    ''' <summary>圆角进度条模板。</summary>
    Private Shared Function RoundedProgressTemplate() As ControlTemplate
        Dim trk As New FrameworkElementFactory(GetType(Border))
        trk.SetValue(Border.CornerRadiusProperty, New CornerRadius(3))
        trk.SetValue(Border.BackgroundProperty, New TemplateBindingExtension(Control.BackgroundProperty))

        Dim ind As New FrameworkElementFactory(GetType(Border))
        ind.SetValue(Border.CornerRadiusProperty, New CornerRadius(3))
        ind.SetValue(Border.BackgroundProperty, New TemplateBindingExtension(ProgressBar.ForegroundProperty))
        ind.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left)

        Dim grd As New FrameworkElementFactory(GetType(Grid))
        grd.AppendChild(trk)
        grd.AppendChild(ind)

        Dim template As New ControlTemplate(GetType(ProgressBar))
        template.VisualTree = grd
        Return template
    End Function

    Private Sub OnLoaded(sender As Object, e As RoutedEventArgs)
        Log("窗口已加载")
        If _info IsNot Nothing OrElse _checked Then Return
        _checked = True
        _btnUpdate.IsEnabled = False
        Task.Run(
            Function() As UpdateInfo
                Try
                    Return ModUpdate.GetLatestInfo()
                Catch ex As Exception
                    Dispatcher.BeginInvoke(New Action(Sub() FinishWithError(ex)))
                    Return Nothing
                End Try
            End Function).ContinueWith(
            Sub(task)
                Dim info = task.Result
                If info Is Nothing Then Return
                Dispatcher.BeginInvoke(New Action(
                    Sub()
                        If ModUpdate.IsNewer(info) Then
                            _info = info
                            ShowInfo(info)
                        Else
                            _lblVersion.Text = "已是最新版本（" & ModUpdate.CurrentVersion & "）"
                            _btnUpdate.IsEnabled = False
                            _btnLater.Content = "关闭"
                        End If
                    End Sub))
            End Sub)
    End Sub

    Private Sub ShowInfo(info As UpdateInfo)
        _lblVersion.Text = ModUpdate.CurrentVersion & "   →   " & info.Version
        Dim log = If(String.IsNullOrWhiteSpace(info.Changelog), "（本次更新没有提供更新说明）", info.Changelog.Trim())
        _txtLog.Text = log
        _txtLog.Visibility = Visibility.Visible
        _btnUpdate.IsEnabled = True
        _btnUpdate.Content = "立即更新"
    End Sub

#End Region

#Region "更新流程"

    Private Sub OnUpdateClick(sender As Object, e As RoutedEventArgs)
        Log("点击更新按钮：busy=" & _busy.ToString() & "，info=" & If(_info Is Nothing, "Nothing", _info.Version & " / " & _info.DownloadUrl))
        If _busy Then Return
        If _info Is Nothing OrElse Not ModUpdate.IsNewer(_info) Then
            Log("当前无需更新，关闭窗口。")
            Close()
            Return
        End If

        _busy = True

        _btnUpdate.IsEnabled = False
        _btnLater.IsEnabled = False
        _btnUpdate.Content = "正在更新…"

        Dim staged As String = Nothing
        Dim failure As Exception = Nothing

        Task.Run(
            Sub()
                Try
                    _cts = New CancellationTokenSource()
                    Dim token = _cts.Token
                    staged = ModUpdate.Download(_info, Sub(stage As String, pct As Integer, msg As String) Report(stage, pct, msg), token)
                    Log("下载完成：" & staged)
                Catch ex As Exception
                    Log("下载失败：" & ex.GetType().Name & " - " & ex.Message.Replace(vbCrLf, " / "))
                    failure = ex
                End Try
            End Sub).ContinueWith(
            Sub(task)
                Dispatcher.BeginInvoke(New Action(
                    Sub()
                        If failure IsNot Nothing Then
                            FinishWithError(failure)
                            Return
                        End If
                        Try
                            If ModUpdate.ApplyAndRestart(_info, staged, Sub(stage As String, pct As Integer, msg As String) Report(stage, pct, msg)) Then
                                SetStep(4, 2)
                                _lblStage.Text = "更新完成，正在重启启动器…"
                                ShutdownHost()
                            Else
                                FinishWithError(New UpdateException("更新未能应用。"))
                            End If
                        Catch ex As Exception
                            FinishWithError(ex)
                        End Try
                    End Sub))
            End Sub)
    End Sub

    Private Sub Report(stage As String, percent As Integer, message As String)
        If Not Dispatcher.CheckAccess() Then
            Dispatcher.BeginInvoke(Sub() Report(stage, percent, message))
            Return
        End If
        _stepPanel.Visibility = Visibility.Visible
        _bar.Visibility = Visibility.Visible
        _lblStage.Visibility = Visibility.Visible
        _lblStage.Text = message
        Select Case stage
            Case "Check"
                SetStep(0, 1)
                _lblVersion.Text = message
            Case "Download"
                SetStep(0, 2)
                SetStep(1, 1)
                _bar.Value = Math.Max(0, Math.Min(100, percent))
                _lblVersion.Text = "正在下载更新包 " & percent & "%"
            Case "Verify"
                SetStep(1, 2)
                SetStep(2, 1)
                _bar.Value = Math.Max(0, Math.Min(100, percent))
                If percent >= 100 Then SetStep(2, 2)
            Case "Extract"
                SetStep(2, 2)
                SetStep(3, 1)
                _bar.Value = Math.Max(0, Math.Min(100, percent))
            Case "Apply"
                SetStep(3, 2)
                SetStep(4, 1)
                _bar.Value = Math.Max(0, Math.Min(100, percent))
        End Select
    End Sub

    ''' <summary>设置操作进程里某个步骤的状态：0 未开始、1 进行中、2 完成、3 失败。</summary>
    Private Sub SetStep(index As Integer, state As Integer)
        If index < 0 OrElse index >= _stepLabels.Count Then Return
        Dim lbl = _stepLabels(index)
        Select Case state
            Case 1
                lbl.Text = "› " & StepNames(index) & "…"
                lbl.Foreground = AccentBrush
                lbl.FontWeight = FontWeights.SemiBold
            Case 2
                lbl.Text = "✔ " & StepNames(index)
                lbl.Foreground = New SolidColorBrush(Color.FromRgb(&H2F, &HA8, &H4F))
                lbl.FontWeight = FontWeights.Normal
            Case 3
                lbl.Text = "✖ " & StepNames(index)
                lbl.Foreground = New SolidColorBrush(Color.FromRgb(&HD9, &H3B, &H3B))
                lbl.FontWeight = FontWeights.Normal
            Case Else
                lbl.Text = "○ " & StepNames(index)
                lbl.Foreground = TextSub
                lbl.FontWeight = FontWeights.Normal
        End Select
    End Sub

    Private Sub FinishWithError(ex As Exception)
        Log("显示错误：" & ex.Message.Replace(vbCrLf, " / "))
        _busy = False
        If _currentStep >= 0 Then SetStep(_currentStep, 3)
        _btnUpdate.IsEnabled = True
        _btnUpdate.Content = "重试"
        _btnLater.IsEnabled = True
        _bar.Value = 0
        _bar.Visibility = Visibility.Collapsed
        _lblStage.Visibility = Visibility.Visible
        _lblStage.Text = "更新失败：" & ex.Message
        MessageBox.Show(Me, ex.Message, "更新失败", MessageBoxButton.OK, MessageBoxImage.Error)
    End Sub

    Private Sub ShutdownHost()
        Try
            If Application.Current IsNot Nothing Then
                Application.Current.Shutdown()
            Else
                Environment.Exit(0)
            End If
        Catch
            Environment.Exit(0)
        End Try
    End Sub

    Private Sub WindowClosingHandler(sender As Object, e As System.ComponentModel.CancelEventArgs)
        Log("收到 Closing 事件，Cancel=" & e.Cancel.ToString())
        Try
            If _cts IsNot Nothing Then _cts.Cancel()
        Catch
        End Try
    End Sub

#End Region

#Region "入口"

    ''' <summary>
    ''' PCL 启动时调用：检查到新版本则弹出更新窗口，返回 True 表示已弹出（调用方可在其关闭后继续正常启动）。
    ''' </summary>
    Public Shared Function CheckAndShow(Optional autoCheck As Boolean = True) As Boolean
        If Not autoCheck Then Return False
        Try
            Dim info = ModUpdate.GetLatestInfo()
            If Not ModUpdate.IsNewer(info) Then Return False
            ShowUpdate(info)
            Return True
        Catch
            Return False
        End Try
    End Function

    ''' <summary>
    ''' 在 UI 线程上以模态方式打开更新窗口。
    ''' 全局只允许一个实例：无论被调用多少次（连点按钮、启动自检与手动检查撞车），都只会显示一个窗口。
    ''' </summary>
    Public Shared Sub ShowUpdate(info As UpdateInfo)
        If Application.Current Is Nothing Then Return
        If Not Application.Current.Dispatcher.CheckAccess() Then
            Application.Current.Dispatcher.Invoke(New Action(Sub() ShowUpdate(info)))
            Return
        End If
        '已经开着更新窗口就不要再开第二个（此刻可能尚未完成首次渲染，因此不能判断 IsVisible）
        If _current IsNot Nothing Then
            Try
                _current.Activate()
            Catch
            End Try
            Return
        End If
        Dim w As New WindowUpdate(info)
        _current = w
        Try
            Log("显示更新窗口：目标 " & info.Version & "，当前 " & ModUpdate.CurrentVersion)
            w.ShowDialog()
            Log("更新窗口已关闭")
        Catch ex As Exception
            Log("更新窗口异常：" & ex.GetType().Name & " - " & ex.Message)
            Throw
        Finally
            _current = Nothing
        End Try
    End Sub

    ''' <summary>阻塞式检查并在有更新时显示窗口（供后台线程调用：网络检查留在后台，只有弹窗回到 UI 线程）。</summary>
    Public Shared Function RunIfUpdateAvailable() As Boolean
        Dim app = Application.Current
        If app Is Nothing Then Return False
        '已经有更新窗口开着，就不再重复检查/弹窗
        If _current IsNot Nothing Then Return False
        '网络请求放在调用线程（后台线程）执行，避免卡住界面
        Dim info As UpdateInfo
        Try
            info = ModUpdate.GetLatestInfo()
            If Not ModUpdate.IsNewer(info) Then
                Log("后台检查更新：已是最新版本（云端 " & info.Version & "）")
                Return False
            End If
            Log("后台检查更新：发现新版本 " & info.Version)
        Catch ex As Exception
            Log("后台检查更新失败：" & ex.Message)
            Return False
        End Try
        app.Dispatcher.Invoke(New Action(
            Sub()
                ShowUpdate(info)
            End Sub))
        Return True
    End Function

#End Region

End Class
