using System.Runtime.InteropServices;
using GlassDock.Logging;

namespace GlassDock.Glass;

/// <summary>
/// Compiles HLSL with d3dcompiler_47.dll, which ships with Windows, so the build needs no fxc and the
/// repository carries no opaque shader binaries.
/// </summary>
internal static class ShaderCompiler
{
    private const uint D3DCOMPILE_OPTIMIZATION_LEVEL3 = 1 << 15;

    public static byte[]? Compile(string source, string name, string entry, string target)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(source);
        int hr;
        ID3DBlob? code = null, errors = null;
        try
        {
            hr = D3DCompile(bytes, (nuint)bytes.Length, name, 0, 0, entry, target, D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, out code, out errors);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Warn("d3dcompiler_47.dll is not available; liquid glass is disabled", ex);
            return null;
        }
        try
        {
            if (hr < 0 || code == null)
            {
                string msg = errors != null ? Marshal.PtrToStringAnsi(errors.GetBufferPointer(), (int)errors.GetBufferSize()) ?? "" : "";
                Log.Warn($"Compiling {name} failed (0x{hr:X8}): {msg.Trim()}");
                return null;
            }
            var result = new byte[(int)code.GetBufferSize()];
            Marshal.Copy(code.GetBufferPointer(), result, 0, result.Length);
            return result;
        }
        finally
        {
            if (code != null) Marshal.ReleaseComObject(code);
            if (errors != null) Marshal.ReleaseComObject(errors);
        }
    }

    [ComImport, Guid("8BA5FB08-5195-40E2-AC58-0D989C3A0102"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ID3DBlob
    {
        [PreserveSig] nint GetBufferPointer();
        [PreserveSig] nuint GetBufferSize();
    }

    [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi, BestFitMapping = false)]
    private static extern int D3DCompile(byte[] src, nuint srcSize, string sourceName, nint defines, nint include,
        string entryPoint, string target, uint flags1, uint flags2, out ID3DBlob? code, out ID3DBlob? errors);
}
