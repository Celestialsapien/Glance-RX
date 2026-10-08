// Glance - a high-performance PDF reader
// Copyright (C) 2026 Adrian
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.
//
// You should have received a copy of the GNU Affero General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.
// SPDX-License-Identifier: AGPL-3.0

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
