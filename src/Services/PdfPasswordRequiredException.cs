using System;

namespace Glance.Services;

/// <summary>
/// Thrown by <see cref="PdfRenderService.InitializeAsync"/> when the target PDF
/// is encrypted and no (correct) password was supplied. Callers should prompt
/// the user for a password and retry the call.
/// </summary>
public class PdfPasswordRequiredException : Exception
{
    public PdfPasswordRequiredException()
        : base("The PDF is password protected and requires a password to open.") { }

    public PdfPasswordRequiredException(string message)
        : base(message) { }
}
