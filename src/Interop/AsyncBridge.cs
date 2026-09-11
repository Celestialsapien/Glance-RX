using System;
using System.Threading;
using System.Threading.Tasks;

namespace Glance.Interop;

/// <summary>
/// Bridge between C# async/await and synchronous Rust FFI calls.
/// Offloads FFI calls to thread pool to prevent UI blocking.
/// </summary>
public static class AsyncBridge
{
    /// <summary>
    /// Run a synchronous native FFI call on the thread pool asynchronously.
    /// This prevents blocking the UI thread during long operations.
    /// </summary>
    /// <typeparam name="T">Return type of native call</typeparam>
    /// <param name="nativeCall">The native FFI function to call (as lambda)</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Task that completes when native call returns</returns>
    public static async Task<T> RunNativeAsync<T>(
        Func<T> nativeCall,
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(nativeCall, cancellationToken);
    }

    /// <summary>
    /// Run a synchronous native FFI call on the thread pool asynchronously (no return value).
    /// </summary>
    /// <param name="nativeCall">The native FFI action to call (as lambda)</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Task that completes when native call returns</returns>
    public static async Task RunNativeAsync(
        Action nativeCall,
        CancellationToken cancellationToken = default)
    {
        await Task.Run(nativeCall, cancellationToken);
    }
}