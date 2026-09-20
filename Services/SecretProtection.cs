using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace sumi.Services;

/// <summary>
/// Protects user secrets with Windows DPAPI, scoped to the current Windows user.
/// </summary>
internal static class SecretProtection
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DataBlob pDataIn,
        string? szDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn,
        IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DataBlob pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    public static string Protect(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        byte[] input = Encoding.UTF8.GetBytes(value);
        IntPtr inputPtr = Marshal.AllocHGlobal(input.Length);
        try
        {
            Marshal.Copy(input, 0, inputPtr, input.Length);
            var inputBlob = new DataBlob { cbData = input.Length, pbData = inputPtr };
            if (!CryptProtectData(ref inputBlob, "sumi API key", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out var outputBlob))
            {
                throw new CryptographicException(Marshal.GetLastWin32Error());
            }

            try
            {
                byte[] protectedBytes = new byte[outputBlob.cbData];
                Marshal.Copy(outputBlob.pbData, protectedBytes, 0, protectedBytes.Length);
                return Convert.ToBase64String(protectedBytes);
            }
            finally
            {
                LocalFree(outputBlob.pbData);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(inputPtr);
        }
    }

    public static string Unprotect(string protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue)) return string.Empty;

        byte[] input;
        try
        {
            input = Convert.FromBase64String(protectedValue);
        }
        catch (FormatException)
        {
            return string.Empty;
        }

        IntPtr inputPtr = Marshal.AllocHGlobal(input.Length);
        try
        {
            Marshal.Copy(input, 0, inputPtr, input.Length);
            var inputBlob = new DataBlob { cbData = input.Length, pbData = inputPtr };
            if (!CryptUnprotectData(ref inputBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out var outputBlob))
            {
                return string.Empty;
            }

            try
            {
                byte[] plainBytes = new byte[outputBlob.cbData];
                Marshal.Copy(outputBlob.pbData, plainBytes, 0, plainBytes.Length);
                return Encoding.UTF8.GetString(plainBytes);
            }
            finally
            {
                LocalFree(outputBlob.pbData);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(inputPtr);
        }
    }
}
