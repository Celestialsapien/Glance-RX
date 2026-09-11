using System;
using System.Collections.Generic;

namespace Glance.Services
{
    public static class LocalizationService
    {
        public static string CurrentLanguage { get; private set; } = "en";

        static LocalizationService()
        {
            try
            {
                // Try to get language from Windows user profile preferences first
                var languages = Windows.System.UserProfile.GlobalizationPreferences.Languages;
                if (languages != null && languages.Count > 0)
                {
                    string primaryLang = languages[0];
                    if (primaryLang.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
                    {
                        CurrentLanguage = "zh";
                        return;
                    }
                    if (primaryLang.StartsWith("es", StringComparison.OrdinalIgnoreCase))
                    {
                        CurrentLanguage = "es";
                        return;
                    }
                }
            }
            catch { }

            try
            {
                // Fallback to CultureInfo
                var cultureName = System.Globalization.CultureInfo.CurrentUICulture.Name;
                if (cultureName.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
                {
                    CurrentLanguage = "zh";
                }
                else if (cultureName.StartsWith("es", StringComparison.OrdinalIgnoreCase))
                {
                    CurrentLanguage = "es";
                }
            }
            catch { }
        }

        public static bool IsSpanish => CurrentLanguage == "es";
        public static bool IsChinese => CurrentLanguage == "zh";

        // Dictionary of translations
        private static readonly Dictionary<string, Dictionary<string, string>> Translations = new()
        {
            ["es"] = new()
            {
                ["ToggleSidebar"] = "Mostrar/Ocultar índice",
                ["OpenPdfFile"] = "Abrir archivo PDF",
                ["Open"] = "Abrir",
                ["PrevPage"] = "Página anterior",
                ["NextPage"] = "Página siguiente",
                ["ReadMode"] = "Modo Lectura / Navegar",
                ["PenMode"] = "Modo Lápiz / Escritura libre",
                ["ColorYellow"] = "Color Amarillo",
                ["ColorGreen"] = "Color Verde",
                ["ColorCyan"] = "Color Celeste",
                ["ColorMagenta"] = "Color Rosa",
                ["ColorRed"] = "Color Rojo",
                ["ColorBlue"] = "Color Azul",
                ["ColorBlack"] = "Color Negro",
                ["Thickness"] = "Grosor:",
                ["Opacity"] = "Opacidad:",
                ["Rotate"] = "Rotar vista (90°)",
                ["LayoutModeVertical"] = "Diseño vertical (cambiar a horizontal)",
                ["LayoutModeHorizontal"] = "Diseño horizontal (cambiar a vertical)",
                ["SplitView"] = "Vista Dividida (Comparar PDFs)",
                ["SaveOriginal"] = "Guardar en el archivo original",
                ["Settings"] = "Configuración",
                ["Print"] = "Imprimir",
                ["PrintTooltip"] = "Imprimir este documento",
                ["PrintPages"] = "Páginas",
                ["PrintPagesAll"] = "Todas",
                ["PrintPagesCurrent"] = "Página actual",
                ["PrintPagesCustom"] = "Rango personalizado",
                ["PrintPageRange"] = "Rango de páginas personalizado",
                ["PrintNUp"] = "Páginas por hoja",
                ["PrintScale"] = "Escala de impresión",
                ["PrintScaleFit"] = "Ajustar a la página",
                ["PrintScaleFill"] = "Rellenar página",
                ["PrintScaleActual"] = "Tamaño real",
                ["PrintScaleCustom"] = "Personalizado",
                ["PrintScaleCustomLabel"] = "Escala personalizada (%)",
                ["PrintErrorTitle"] = "Error de impresión",
                ["PrintErrorContent"] = "No se pudo imprimir el PDF: {0}",
                ["NoPrinterTitle"] = "No se encontró impresora",
                ["NoPrinterContent"] = "No hay ninguna impresora disponible. Instala una impresora para poder imprimir.",
                ["AppTheme"] = "Tema de la aplicación",
                ["ThemeLight"] = "Claro",
                ["ThemeDark"] = "Oscuro",
                ["ThemeSystem"] = "Predeterminado del sistema",
                ["AboutTitle"] = "Acerca de Glance",
                ["Developer"] = "Desarrollado por jonas1ara",
                ["WebsiteLink"] = "Sitio Web Oficial",
                ["GitHubLink"] = "Repositorio de GitHub",
                ["WelcomeSubtitle"] = "Abre un documento para comenzar.",
                ["WelcomeOpenButton"] = "Abrir Documento PDF",
                ["Thumbnails"] = "Miniaturas",
                ["OpenSecondPdf"] = "Abrir segundo PDF",
                ["OpenSecondPdfText"] = "Abrir PDF para Comparación",
                ["SideBySideComparer"] = "Comparador Lado a Lado",
                ["CloseComparison"] = "Cerrar Comparación",
                ["UnsavedChangesTitle"] = "Cambios sin guardar",
                ["UnsavedChangesContent"] = "¿Deseas guardar los cambios en el archivo PDF antes de salir?",
                ["SaveAndExit"] = "Guardar y Salir",
                ["ExitWithoutSaving"] = "Salir sin guardar",
                ["Cancel"] = "Cancelar",
                ["Home"] = "Inicio",
                ["HomeTooltip"] = "Inicio",
                ["FitWidth"] = "Ajustar Ancho",
                ["ZoomOut"] = "Alejar (10%)",
                ["ZoomIn"] = "Acercar (10%)",
                ["QuickZoom"] = "Zoom rápido",
                ["Error"] = "Error",
                ["Stack"] = "Pila",
                ["Ok"] = "Aceptar",
                ["PdfLoadErrorTitle"] = "Error al cargar PDF",
                ["PdfLoadErrorContent"] = "No se pudo cargar el PDF: {0}",
                ["PasswordTitle"] = "Contraseña requerida",
                ["PasswordTitleRetry"] = "Contraseña incorrecta",
                ["PasswordMessage"] = "\"{0}\" está protegido con contraseña. Introduce la contraseña para abrirlo.",
                ["PasswordMessageRetry"] = "La contraseña introducida es incorrecta. Inténtalo de nuevo.",
                ["PasswordPlaceholder"] = "Contraseña del documento",
                ["OverwriteFailedTitle"] = "No se pudo sobrescribir",
                ["OverwriteFailedContent"] = "No se pudo sobrescribir el archivo original. ¿Deseas guardar una copia en otra ubicación?",
                ["SaveCopy"] = "Guardar Copia",
                ["ExportSuccessTitle"] = "Exportación Exitosa",
                ["ExportSuccessContent"] = "El archivo PDF con tus firmas y anotaciones se ha guardado en:\n\n{0}",
                ["ExportErrorTitle"] = "Error de Exportación",
                ["ExportErrorContent"] = "No se pudo exportar el PDF: {0}",
                ["SaveToastSuccess"] = "Guardado",
                ["SaveToastFailed"] = "No se pudo guardar",
                ["SaveAsToastSuccess"] = "Copia guardada",
                ["SaveAsToastFailed"] = "No se pudo guardar la copia"
            },
            ["zh"] = new()
            {
                ["ToggleSidebar"] = "显示/隐藏侧边栏",
                ["OpenPdfFile"] = "打开PDF文件",
                ["Open"] = "打开",
                ["PrevPage"] = "上一页",
                ["NextPage"] = "下一页",
                ["ReadMode"] = "阅读模式 / 导航",
                ["PenMode"] = "笔模式 / 手写",
                ["ColorYellow"] = "黄色",
                ["ColorGreen"] = "绿色",
                ["ColorCyan"] = "青色",
                ["ColorMagenta"] = "洋红色",
                ["ColorRed"] = "红色",
                ["ColorBlue"] = "蓝色",
                ["ColorBlack"] = "黑色",
                ["Thickness"] = "粗细：",
                ["Opacity"] = "不透明度：",
                ["Rotate"] = "旋转视图 (90°)",
                ["LayoutModeVertical"] = "纵向布局（点击切换为横向）",
                ["LayoutModeHorizontal"] = "横向布局（点击切换为纵向）",
                ["SplitView"] = "分屏视图 (对比PDF)",
                ["SaveOriginal"] = "保存到原始文件",
                ["Settings"] = "设置",
                ["Print"] = "打印",
                ["PrintTooltip"] = "打印此文档",
                ["PrintPages"] = "打印范围",
                ["PrintPagesAll"] = "全部",
                ["PrintPagesCurrent"] = "当前页",
                ["PrintPagesCustom"] = "自定义范围",
                ["PrintPageRange"] = "自定义页码范围",
                ["PrintNUp"] = "每版打印页数",
                ["PrintScale"] = "缩放打印",
                ["PrintScaleFit"] = "适应页面",
                ["PrintScaleFill"] = "填充页面",
                ["PrintScaleActual"] = "原始大小",
                ["PrintScaleCustom"] = "自定义",
                ["PrintScaleCustomLabel"] = "自定义缩放 (%)",
                ["PrintErrorTitle"] = "打印失败",
                ["PrintErrorContent"] = "无法打印 PDF：{0}",
                ["NoPrinterTitle"] = "未找到打印机",
                ["NoPrinterContent"] = "系统中没有可用的打印机，请先安装打印机后再打印。",
                ["AppTheme"] = "应用主题",
                ["ThemeLight"] = "浅色",
                ["ThemeDark"] = "深色",
                ["ThemeSystem"] = "跟随系统",
                ["AboutTitle"] = "关于 Glance",
                ["Developer"] = "由 jonas1ara 开发",
                ["WebsiteLink"] = "官方网站",
                ["GitHubLink"] = "GitHub 仓库",
                ["WelcomeSubtitle"] = "打开文档开始使用。",
                ["WelcomeOpenButton"] = "打开PDF文档",
                ["Thumbnails"] = "缩略图",
                ["OpenSecondPdf"] = "打开第二个PDF",
                ["OpenSecondPdfText"] = "打开PDF进行对比",
                ["SideBySideComparer"] = "并排比较器",
                ["CloseComparison"] = "关闭比较",
                ["UnsavedChangesTitle"] = "未保存的更改",
                ["UnsavedChangesContent"] = "是否在退出前保存对PDF文件的更改？",
                ["SaveAndExit"] = "保存并退出",
                ["ExitWithoutSaving"] = "不保存直接退出",
                ["Cancel"] = "取消",
                ["Home"] = "首页",
                ["HomeTooltip"] = "首页",
                ["FitWidth"] = "适应宽度",
                ["ZoomOut"] = "缩小 (10%)",
                ["ZoomIn"] = "放大 (10%)",
                ["QuickZoom"] = "快速缩放",
                ["Error"] = "错误",
                ["Stack"] = "堆栈",
                ["Ok"] = "确定",
                ["PdfLoadErrorTitle"] = "PDF 加载失败",
                ["PdfLoadErrorContent"] = "无法加载 PDF：{0}",
                ["PasswordTitle"] = "需要密码",
                ["PasswordTitleRetry"] = "密码错误",
                ["PasswordMessage"] = "\"{0}\" 已加密，请输入密码以打开。",
                ["PasswordMessageRetry"] = "您输入的密码不正确，请重试。",
                ["PasswordPlaceholder"] = "文档密码",
                ["OverwriteFailedTitle"] = "无法覆盖原文件",
                ["OverwriteFailedContent"] = "无法覆盖原文件，是否另存为副本？",
                ["SaveCopy"] = "保存副本",
                ["ExportSuccessTitle"] = "导出成功",
                ["ExportSuccessContent"] = "带有签名和批注的 PDF 已保存至：\n\n{0}",
                ["ExportErrorTitle"] = "导出失败",
                ["ExportErrorContent"] = "无法导出 PDF：{0}",
                ["SaveToastSuccess"] = "保存成功",
                ["SaveToastFailed"] = "保存失败",
                ["SaveAsToastSuccess"] = "另存为成功",
                ["SaveAsToastFailed"] = "另存为失败"
            },
            ["en"] = new()
            {
                ["ToggleSidebar"] = "Show/Hide index",
                ["OpenPdfFile"] = "Open PDF file",
                ["Open"] = "Open",
                ["PrevPage"] = "Previous page",
                ["NextPage"] = "Next page",
                ["ReadMode"] = "Read Mode / Navigate",
                ["PenMode"] = "Pen Mode / Freehand drawing",
                ["ColorYellow"] = "Yellow Color",
                ["ColorGreen"] = "Green Color",
                ["ColorCyan"] = "Cyan Color",
                ["ColorMagenta"] = "Magenta Color",
                ["ColorRed"] = "Red Color",
                ["ColorBlue"] = "Blue Color",
                ["ColorBlack"] = "Black Color",
                ["Thickness"] = "Thickness:",
                ["Opacity"] = "Opacity:",
                ["Rotate"] = "Rotate view (90°)",
                ["LayoutModeVertical"] = "Vertical layout (switch to horizontal)",
                ["LayoutModeHorizontal"] = "Horizontal layout (switch to vertical)",
                ["SplitView"] = "Split View (Compare PDFs)",
                ["SaveOriginal"] = "Save to original file",
                ["Settings"] = "Settings",
                ["Print"] = "Print",
                ["PrintTooltip"] = "Print this document",
                ["PrintPages"] = "Pages",
                ["PrintPagesAll"] = "All",
                ["PrintPagesCurrent"] = "Current page",
                ["PrintPagesCustom"] = "Custom range",
                ["PrintPageRange"] = "Custom page range",
                ["PrintNUp"] = "Pages per sheet",
                ["PrintScale"] = "Print scaling",
                ["PrintScaleFit"] = "Fit to page",
                ["PrintScaleFill"] = "Fill page",
                ["PrintScaleActual"] = "Actual size",
                ["PrintScaleCustom"] = "Custom",
                ["PrintScaleCustomLabel"] = "Custom scale (%)",
                ["PrintErrorTitle"] = "Print Failed",
                ["PrintErrorContent"] = "Failed to print the PDF: {0}",
                ["NoPrinterTitle"] = "No Printer Found",
                ["NoPrinterContent"] = "There is no printer available. Please install a printer before printing.",
                ["AppTheme"] = "App Theme",
                ["ThemeLight"] = "Light",
                ["ThemeDark"] = "Dark",
                ["ThemeSystem"] = "System default",
                ["AboutTitle"] = "About Glance",
                ["Developer"] = "Developed by jonas1ara",
                ["WebsiteLink"] = "Official Website",
                ["GitHubLink"] = "GitHub Repository",
                ["WelcomeSubtitle"] = "Open a document to get started.",
                ["WelcomeOpenButton"] = "Open PDF Document",
                ["Thumbnails"] = "Thumbnails",
                ["OpenSecondPdf"] = "Open second PDF",
                ["OpenSecondPdfText"] = "Open PDF for Comparison",
                ["SideBySideComparer"] = "Side-by-Side Comparer",
                ["CloseComparison"] = "Close Comparison",
                ["UnsavedChangesTitle"] = "Unsaved Changes",
                ["UnsavedChangesContent"] = "Do you want to save the changes to the PDF file before exiting?",
                ["SaveAndExit"] = "Save and Exit",
                ["ExitWithoutSaving"] = "Exit without saving",
                ["Cancel"] = "Cancel",
                ["Home"] = "Home",
                ["HomeTooltip"] = "Home",
                ["FitWidth"] = "Fit Width",
                ["ZoomOut"] = "Zoom out (10%)",
                ["ZoomIn"] = "Zoom in (10%)",
                ["QuickZoom"] = "Quick zoom",
                ["Error"] = "Error",
                ["Stack"] = "Stack",
                ["Ok"] = "OK",
                ["PdfLoadErrorTitle"] = "PDF Load Failed",
                ["PdfLoadErrorContent"] = "Failed to load PDF: {0}",
                ["PasswordTitle"] = "Password required",
                ["PasswordTitleRetry"] = "Incorrect password",
                ["PasswordMessage"] = "\"{0}\" is password protected. Enter the password to open it.",
                ["PasswordMessageRetry"] = "The password you entered is incorrect. Please try again.",
                ["PasswordPlaceholder"] = "Document password",
                ["OverwriteFailedTitle"] = "Could not overwrite",
                ["OverwriteFailedContent"] = "Could not overwrite the original file. Do you want to save a copy elsewhere?",
                ["SaveCopy"] = "Save Copy",
                ["ExportSuccessTitle"] = "Export Successful",
                ["ExportSuccessContent"] = "The PDF with your signatures and annotations has been saved to:\n\n{0}",
                ["ExportErrorTitle"] = "Export Failed",
                ["ExportErrorContent"] = "Could not export the PDF: {0}",
                ["SaveToastSuccess"] = "Saved",
                ["SaveToastFailed"] = "Save failed",
                ["SaveAsToastSuccess"] = "Saved a copy",
                ["SaveAsToastFailed"] = "Save as failed"
            }
        };

        public static string Get(string key)
        {
            string lang = IsChinese ? "zh" : IsSpanish ? "es" : "en";
            if (Translations[lang].TryGetValue(key, out string? val) && val != null)
            {
                return val;
            }
            // Fallback to English if key not found
            if (Translations["en"].TryGetValue(key, out string? fallbackVal) && fallbackVal != null)
            {
                return fallbackVal;
            }
            return key;
        }
    }
}