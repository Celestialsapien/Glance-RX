# 按钮交互状态色 全量审计（2026-09-26）

> 目的：把「WinUI3 默认给了哪些状态色」和「Glance 里每一枚可点控件当前处于哪种状态」彻底扒清，
> 为下一轮改配色提供逐条对照表。**本次不改动任何代码。**

---

## 0. 结论先行

| 项 | 发现 |
| --- | --- |
| 可点控件范围 | 全部在 `src/MainPage.xaml`（共 35 枚）。`MainWindow.xaml` 无按钮；系统标题栏 3 枚按钮在 `MainWindow.xaml.cs:554-577` 单独定制（已打平为 hover/pressed 两档） |
| 全局色板 | **不存在**。`App.xaml` 的 `ResourceDictionary` 是空的，只合并了 `XamlControlsResources`；`MainPage.xaml:15-18` 那两个 `Style` 是**空壳**（`BasedOn` 默认样式，内部无 Setter） |
| 已做的设计干涉 | 只有 3 类散点式覆盖：① C# 侧 `element.Resources["..."]` 覆盖模板资源键；② 若干 `Background="Transparent" BorderThickness="0"`；③ 颜色/粗细按钮直接写 `Background` DP |
| 状态数 | `Button` 4 档；`ToggleButton` 12 档（CommonStates 4 + Checked 4 + Indeterminate 4，**同一个 VisualStateGroup 内互斥**） |

---

## 1. WinUI3 默认到底给了几档（实证来源：本机 NuGet 包内的 generic.xaml）

权威文件：`C:\Users\60455\.nuget\packages\microsoft.windowsappsdk.winui\2.2.1\lib\net6.0-windows10.0.17763.0\Microsoft.WinUI\Themes\generic.xaml`

### 1.1 `DefaultButtonStyle`（`TargetType="Button"`）

Setter 级默认值：

| 属性 | 资源键 | 别名到 |
| --- | --- | --- |
| Background | `ButtonBackground` | `ControlFillColorDefaultBrush` |
| Foreground | `ButtonForeground` | `TextFillColorPrimaryBrush` |
| BorderBrush | `ButtonBorderBrush` | `ControlElevationBorderBrush`（一条 0→3px 的竖向渐变笔） |

模板 `CommonStates` 视觉状态（状态写在 `ContentPresenter` 上，不是按钮自身）：

| VisualState | ContentPresenter.Background | ContentPresenter.Foreground | ContentPresenter.BorderBrush |
| --- | --- | --- | --- |
| `Normal` | `ButtonBackground` → `ControlFillColorDefault` | `ButtonForeground` | `ButtonBorderBrush` |
| `PointerOver` | `ButtonBackgroundPointerOver` → `ControlFillColorSecondary` | `ButtonForegroundPointerOver` | `ButtonBorderBrushPointerOver` |
| `Pressed` | `ButtonBackgroundPressed` → `ControlFillColorTertiary` | `ButtonForegroundPressed` | `ButtonBorderBrushPressed` |
| `Disabled` | `ButtonBackgroundDisabled` → `ControlFillColorDisabled` | `ButtonForegroundDisabled` | `ButtonBorderBrushDisabled` |

Light 档实测色值（`ControlFillColor*` 定义值，AARRGGBB）：

| 键 | 值 | alpha |
| --- | --- | --- |
| ControlFillColorDefault | `#B3FFFFFF` | 179/255 ≈ 70% |
| ControlFillColorSecondary | `#80F9F9F9` | 128/255 ≈ 50% |
| ControlFillColorTertiary | `#4DF9F9F9` | 77/255 ≈ 30% |
| ControlFillColorDisabled | `#4DF9F9F9` | 30% |

⇒ **普通 Button 就有 3 档背景（70% / 50% / 30% 白）+ 2 档前景 + 2 档描边**，鼠标在按钮上按下再抬起，会走
`Pressed(30%) → PointerOver(50%) → Normal(70%)`，同一块地方连变三次。

### 1.2 `DefaultToggleButtonStyle`（`TargetType="ToggleButton"`）

Base 四档与 Button 同构，但在**同一个 `CommonStates` 组**里追加 8 档：

| VisualState | Background 资源键 | 别名 | 备注 |
| --- | --- | --- | --- |
| `Normal` | `ToggleButtonBackground` | `ControlFillColorDefaultBrush` | |
| `PointerOver` | `ToggleButtonBackgroundPointerOver` | `ControlFillColorSecondaryBrush` | |
| `Pressed` | `ToggleButtonBackgroundPressed` | `ControlFillColorTertiaryBrush` | |
| `Disabled` | `ToggleButtonBackgroundDisabled` | `ControlFillColorDisabledBrush` | |
| **`Checked`** | `ToggleButtonBackgroundChecked` | **`AccentFillColorDefaultBrush`** | 系统强调色，100% |
| **`CheckedPointerOver`** | `ToggleButtonBackgroundCheckedPointerOver` | `AccentFillColorSecondaryBrush` | 系统强调色 + `Opacity=0.9` |
| **`CheckedPressed`** | `ToggleButtonBackgroundCheckedPressed` | `AccentFillColorTertiaryBrush` | 系统强调色 + `Opacity=0.8` |
| **`CheckedDisabled`** | `ToggleButtonBackgroundCheckedDisabled` | `AccentFillColorDisabledBrush` | |
| `Indeterminate` ×4 | `ToggleButtonBackgroundIndeterminate*` | 同一族 | Glance 未使用 |

⇒ **这是"混乱"的主因**：Checked 三档是**系统强调色**（默认蓝 `#0078D4`，用户可在系统「设置 → 个性化 → 主题色」里改），
不透明度 100% → 90% → 80% → 回到 100%。同一枚按钮在「选中悬浮按下选中」之间反复横跳，颜色来回变。
另外 `ToggleButton` 的 Checked 状态还会把 `BackgroundSizing` 切成 `ToggleButtonCheckedStateBackgroundSizing`（内边框对齐），
即边框几何也跟着变一次。

---

## 2. Glance 每一枚可点控件清单

图例：**4 档** = 默认 Button 全量；**8 档** = 默认 ToggleButton（未选中 4 + 选中 4，未做覆盖）；
**3 档(已打平)** = Normal/PointerOver/Pressed 颜色恒定，不再区分；**4 档(Transparent 无效)** = 写了
`Background="Transparent"` 但仍会浮出模板色（见 §3.1）。

| # | 控件 | 位置 | 类型 | 当前对状态色的干预 | 实际生效档数 |
| --- | --- | --- | --- | --- | --- |
| 1 | ToggleSidebarButton | `MainPage.xaml:178` | Button | 无 | 4 档（默认） |
| 2 | PrevPageButton | `:185` | Button | 无 | 4 |
| 3 | NextPageButton | `:200` | Button | 无 | 4 |
| 4 | ReadModeButton | `:208` | ToggleButton | 无（IsChecked 由 C# 同步，`:3758`） | 8 |
| 5 | **PenModeButton** | `:211` | ToggleButton | **C# `:615-627` 覆盖 9 个 `ToggleButton*Checked*` 资源键** → Checked 三态恒为画笔色 | 选中 3 + 未选中 4 = 复用 7 |
| 6 | RotateButton | `:220` | Button | 无 | 4 |
| 7 | LayoutToggleButton | `:225` | Button | 无（图标由 `UpdateLayoutToggleVisual()` 旋转） | 4 |
| 8 | SplitViewToggleButton | `:230` | ToggleButton | 无；初始 `Collapsed`，`:1737` 按条件显示 | 8 |
| 9 | SaveButton | `:235` | Button | 无（初始 Collapsed） | 4 |
| 10 | PrintButton | `:240` | Button | 无（初始 Collapsed） | 4 |
| 11 | SettingsButton | `:245` | Button | 无；Flyout 内 2 枚 HyperlinkButton | 4 |
| 12 | WebsiteLink | `:273` | HyperlinkButton | 无 | 自身 Hover/Pressed 两档（强调色文字） |
| 13 | GitHubLink | `:274` | HyperlinkButton | 无 | 同上 |
| 14 | ColorYellow/Green/Cyan/Magenta/Red/Blue/Black | `:317-323` | Button | `Background` 硬编码色 + C# `:4592-4593` 把 `ButtonBackgroundPointerOver/Pressed` 覆盖为同色；`:4528-4529`、`:4564-4565` 把 `ButtonBorderBrush{PointerOver,Pressed}` 覆盖为透明 | **3 档已打平**（选中/悬浮/按下同色，仅靠 1.72× 缩放区分） |
| 15 | Thickness1/2/3.5/7/14 | `:336,342,348,354,360` | Button | C# `:4508-4512` 直接写 `Background` DP + 覆盖 `ButtonBackgroundPointerOver/Pressed` 为同一色 | **3 档已打平** |
| 16 | 打开 PDF（欢迎页） | `:378` | Button | `Style="{ThemeResource AccentButtonStyle}"` | 4（强调色实心） |
| 17 | ModeThumbnailsButton | `:418` | ToggleButton | 无 | 8 |
| 18 | ModeBookmarksButton | `:421` | ToggleButton | 无 | 8 |
| 19 | ModeOutlineButton | `:424` | ToggleButton | 无 | 8 |
| 20 | AddBookmarkButton | `:448` | Button | 无（FontIcon 前景硬编码 `#F2C200`） | 4 |
| 21 | 书签行内「编辑/确认/取消/删除」 | `:550,553,556,559` | Button | `Background="Transparent" BorderThickness="0"` | **4 档（Transparent 不屏蔽 Over/Pressed）** |
| 22 | OpenSecondPdfButton | `:653` | Button | `Background="Transparent" BorderThickness="0"` | **4 档** |
| 23 | CloseSecondPdfButton | `:686` | Button | 同上 | **4 档** |
| 24 | ZoomOutButton | `:725` | Button | 同上 | **4 档** |
| 25 | ZoomInButton | `:731` | Button | 同上 | **4 档** |
| 26 | ZoomValueButton | `:737` | Button | 同上（内含 TextBlock） | **4 档** |
| 27 | FitWidthButton | `:760` | Button | 同上 | **4 档** |
| 28-35 | 缩放预设 10%…200% | `:748-755` | Button | `ZoomFlyoutItemStyle`（`:19-27`）：`Background=Transparent`、`BorderThickness=0`、`CornerRadius=0`、显式 Foreground | **4 档**（未覆盖 `ButtonBackgroundPointerOver/Pressed`） |
| 36-38 | 缩略图行内书签星、大纲行、书签行 | `:494` 等 | Grid/ListViewItem | **刻意不用 Button**（见 §3.2） | 容器级 ItemClick 悬浮色另算 |
| 39-41 | 系统标题栏 最小化/最大化/关闭 | `MainWindow.xaml.cs:554-577` | WinUI TitleBar | 已直接写死 hover/pressed 前景背景 | 2 档（白/黑两套写死） |

---

## 3. 决定方案走向的 3 个机制点

### 3.1 `Background="Transparent"` **不等于** 悬浮无提示色

模板里 PointerOver / Pressed 的 `ObjectAnimationUsingKeyFrames` 目标是 **`ContentPresenter.Background`**，
不是按钮自身的 `Background` 属性（按钮 `Background` 只是这些状态的初值来源）。
所以 `:550/653/686/725/731/737/760` 这些写死 `Background="Transparent"` 的图标按钮，
悬浮时仍会被模板塞回 `ControlFillColorSecondary`（Light 档 **50% 白**），按下时 30% 白。
⇒ 想让它们"真 transparent"，必须覆盖 `ButtonBackgroundPointerOver` / `ButtonBackgroundPressed`
（颜色按钮 `:4592-4593` 正是这么做的），而不是只写 `Background="Transparent"`。

### 3.2 缩略图行内的书签星**故意不用 Button**

`MainPage.xaml:487-493` 的注释写明根因：ButtonBase 按下时会 `CapturePointer`，
指针一被捕获，所在 `ListViewItem` 立刻收到 `PointerExited` → 整行悬浮提示色消失（松手才恢复），
表现为"提示色闪烁"。改用不捕获指针的 `Grid` + 手写 PointerPressed/Tapped。
⇒ 这条历史决策直接约束：**如果要把提示色砍掉或加强，缩略图行不能跟着走同一套。**

### 3.3 PenModeButton 的覆盖是"打平 Checked"，不是"统一"

C# `:617-625` 覆盖了 9 个键，把 Checked / CheckedPointerOver / CheckedPressed 三档**全部压成同一个画笔色**，
但：
- 未选中时的 `Normal / PointerOver / Pressed` 仍是默认 3 档（70% / 50% / 30% 白）——**没打平**；
- 前景三档 `:623-625` 压成 `_penForegroundBrush`（按亮度自动黑/白）——打平；
- 覆盖仅在构造期执行一次（`:615-627`），但 `_penHighlightBrush` 会在 `ApplyPenHighlight()` 里**实时改 Color**
  （`:4465`），这是"改一个共享 SolidColorBrush 实例就能让所有态跟着变"的做法。

---

## 4. 顺带记账（不属于按钮，但同一类视觉残留）

- `MainPage.xaml:608-610`：`ScrollBarThumbBackground` / `ScrollBarPanningThumbBackground` /
  `ScrollBarThumbFillDisabled` 三个画刷被写成 `Color="Red"`（占位未改）。
- `MainPage.xaml:462` 起的 `PageListView`：`ListViewItem` 自带 `PointerOver` 悬浮底色
  （同样来自 `ControlFillColor*`），与行内书签星的"刻意无提示"语义互相打架。

---

## 5. 待讨论（**不实施**）

以下只是把问题摆出来供拍板，方案待定：

1. **要不要统一成"一档恒定背景 + 只留悬浮"**，还是"全部打平成零提示"？
2. `Button` 默认 3 档背景（70/50/30% 白）在深色底上是否全部需要？
3. `ToggleButton` 的 Checked 用**系统强调色**（可随系统主题色变）是否保留，还是钉死成自定义色？
   （PenModeButton 已经绕开了强调色，Mode* 三枚还在用。）
4. 悬浮要不要保留"提示"语义（否则用户不知道能点），按下是否可以直接取消反馈。
5. `ZoomFlyoutItemStyle` 的菜单项要不要跟主按钮同规则（目前它只在 Normal 上 transparent，Over/Pressed 仍是默认色）。
6. 是否需要一个统一的 `<Style>`（而不是现在这种"每个按钮各自 Resources 覆盖"的散点式），
   以及放在 `App.xaml` 还是 `MainPage.xaml:14` 起的 `Page.Resources`。
