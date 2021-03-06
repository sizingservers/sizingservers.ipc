﻿/*
Original author: Dieter Vandroemme, dev at Sizing Servers Lab (https://www.sizingservers.be) @ University College of West-Flanders, Department GKG
Written in 2016

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Serialization.Formatters.Binary;
using System.Security.Cryptography;
using System.Text;

namespace SizingServers.IPC {
    /// <summary>
    /// Shared functions for internal and external use.
    /// </summary>
    public static class Shared {
        /// <summary>
        /// Default port for the epms service to listen on.
        /// </summary>
        public const int EPMS_DEFAULT_TCP_PORT = 4455;

        /// <summary>
        /// The amount of memory, in bytes, a 64bit integer takes. (Should be obvious :))
        /// </summary>
        public static readonly int LONGSIZE = Marshal.SizeOf<long>();

        /// <summary>
        /// UTF8
        /// </summary>
        /// <param name="s"></param>
        /// <returns></returns>
        public static byte[] GetBytes(string s) { return Encoding.UTF8.GetBytes(s); }
        /// <summary>
        /// 
        /// </summary>
        /// <param name="l"></param>
        /// <returns></returns>
        public static byte[] GetBytes(long l) {
            int size = Marshal.SizeOf(l);
            byte[] arr = new byte[size];
            IntPtr ptr = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(l, ptr, true);
            Marshal.Copy(ptr, arr, 0, size);
            Marshal.FreeHGlobal(ptr);
            return arr;
        }
        /// <summary>
        /// 
        /// </summary>
        /// <param name="b"></param>
        /// <returns></returns>
        public static byte GetByte(bool b) {
            return (byte)(b ? 1 : 0);
        }
        /// <summary>
        /// 
        /// </summary>
        /// <param name="o"></param>
        /// <param name="bf"></param>
        /// <returns></returns>
        public static byte[] GetBytes(object o, BinaryFormatter bf) {
            byte[] bytes = null;
            using (var ms = new MemoryStream(1)) {
                bf.Serialize(ms, o);
                bytes = ms.GetBuffer();
            }
            return bytes;
        }
        /// <summary>
        /// </summary>
        /// <param name="bytes"></param>
        /// <param name="compressionLevel"></param>
        /// <returns></returns>
        public static byte[] Gzip(byte[] bytes, CompressionLevel compressionLevel = CompressionLevel.Optimal) {
            byte[] gzipped = null;
            using (var ms = new MemoryStream())
            using (var dfStream = new GZipStream(ms, compressionLevel)) {
                dfStream.Write(bytes, 0, bytes.Length);
                gzipped = ms.GetBuffer();
            }
            
            return gzipped;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="bytes"></param>
        /// <returns></returns>
        public static long GetLong(byte[] bytes) {
            long l = Activator.CreateInstance<long>();
            int size = Marshal.SizeOf(l);
            IntPtr ptr = Marshal.AllocHGlobal(size);
            Marshal.Copy(bytes, 0, ptr, size);
            l = (long)Marshal.PtrToStructure(ptr, l.GetType());
            Marshal.FreeHGlobal(ptr);
            return l;
        }
        /// <summary>
        /// UTF8. '\0' gets automatically trimmed.
        /// </summary>
        /// <param name="bytes"></param>
        /// <returns></returns>
        public static string GetString(byte[] bytes) { return Encoding.UTF8.GetString(bytes).Trim('\0'); }
        /// <summary>
        /// 
        /// </summary>
        /// <param name="bytes"></param>
        /// <returns></returns>
        public static bool GetBool(byte[] bytes) { return bytes[0] == 1; }
        /// <summary>
        /// 
        /// </summary>
        /// <param name="bytes"></param>
        /// <param name="bf"></param>
        /// <returns></returns>
        public static object GetObject(byte[] bytes, BinaryFormatter bf) {
            object o;
            using (var ms = new MemoryStream(bytes))
                o = bf.Deserialize(ms);
            return o;
        }
        /// <summary>
        /// </summary>
        /// <param name="bytes"></param>
        /// <returns>
        /// '\0' at the end of the output is possible. Please take this into account.
        /// </returns>
        public static byte[] Ungzip(byte[] bytes) {
            byte[] ungzipped = null;
            using (var readMs = new MemoryStream(bytes))
            using (var dfStream = new GZipStream(readMs, CompressionMode.Decompress)) {
                using (var writeMs = new MemoryStream()) {
                    int read = 0;
                    byte[] buffer = new byte[256];
                    while ((read = dfStream.Read(buffer, 0, buffer.Length)) != 0) 
                        writeMs.Write(buffer, 0, read);

                    ungzipped = writeMs.GetBuffer();
                }
            }
            return ungzipped;
        }

        /// <summary>
        /// Read bytes from a stream.
        /// </summary>
        /// <param name="str"></param>
        /// <param name="bufferSize">The amount of bytes read per go. This could be the receive buffer size of a socket.</param>
        /// <param name="length">The total amount of bytes that should be read.</param>
        /// <returns></returns>
        public static byte[] ReadBytes(Stream str, int bufferSize, long length) {
            var bytes = new byte[length];

            long totalRead = 0;
            while (totalRead != length) {
                int chunkLength = bufferSize;
                if (chunkLength > length - totalRead) chunkLength = (int)(length - totalRead);

                var chunk = new byte[chunkLength];
                int chunkRead = str.Read(chunk, 0, chunkLength);
                if (chunkRead <= 0) break;
                chunk.CopyTo(bytes, totalRead);
                totalRead += chunkRead;
            }

            return bytes;
        }
        /// <summary>
        /// Write bytes to a stream.
        /// </summary>
        /// <param name="str"></param>
        /// <param name="bufferSize">The amount of bytes to write per go. This could be the send buffer size of a socket.</param>
        /// <param name="bytes"></param>
        public static void WriteBytes(Stream str, int bufferSize, byte[] bytes) {
            int offset = 0;
            while (offset != bytes.Length) {
                int length = bufferSize;
                if (offset + length > bytes.Length)
                    length = bytes.Length - offset;

                str.Write(bytes, offset, length);

                offset += length;
            }

            str.Flush();
        }

        /// <summary>
        /// A simple way to encrypt a string. (Rijndael, from utf8 to base 64)
        /// /// Example (don't use this password and salt): Encrypt("secret", "password", new byte[] { 0x59, 0x06, 0x59, 0x3e, 0x21, 0x4e, 0x55, 0x34, 0x96, 0x15, 0x11, 0x13, 0x72 });
        /// </summary>
        /// <param name="toEncrypt"></param>
        /// <param name="password"></param>
        /// <param name="salt"></param>
        /// <returns>The encrypted string. (base 64)</returns>
        public static string Encrypt(string toEncrypt, string password, byte[] salt) {
            var pdb = new PasswordDeriveBytes(password, salt);
            return Convert.ToBase64String(Encrypt(Encoding.UTF8.GetBytes(toEncrypt), pdb.GetBytes(32), pdb.GetBytes(16)));
        }
        private static byte[] Encrypt(byte[] toEncrypt, byte[] key, byte[] IV) {
            var ms = new MemoryStream(1);
            var alg = Rijndael.Create();
            alg.Key = key;
            alg.IV = IV;

            var cs = new CryptoStream(ms, alg.CreateEncryptor(), CryptoStreamMode.Write);
            cs.Write(toEncrypt, 0, toEncrypt.Length);
            cs.Close();
            return ms.ToArray();
        }
        /// <summary>
        /// A simple way to decrypt a string. (Rijndael, from base 64 to utf8)
        /// Example (don't use this password and salt): Decrypt("****", "password", new byte[] { 0x59, 0x06, 0x59, 0x3e, 0x21, 0x4e, 0x55, 0x34, 0x96, 0x15, 0x11, 0x13, 0x72 });
        /// </summary>
        /// <param name="toDecrypt"></param>
        /// <param name="password"></param>
        /// <param name="salt"></param>
        /// <returns>The decrypted string.</returns>
        public static string Decrypt(string toDecrypt, string password, byte[] salt) {
            var pdb = new PasswordDeriveBytes(password, salt);
            return Encoding.UTF8.GetString(Decrypt(Convert.FromBase64String(toDecrypt), pdb.GetBytes(32), pdb.GetBytes(16)));
        }
        private static byte[] Decrypt(byte[] toDecrypt, byte[] Key, byte[] IV) {
            var ms = new MemoryStream(1);
            var alg = Rijndael.Create();
            alg.Key = Key;
            alg.IV = IV;

            var cs = new CryptoStream(ms, alg.CreateDecryptor(), CryptoStreamMode.Write);
            cs.Write(toDecrypt, 0, toDecrypt.Length);
            try {
                cs.Close();
            }
            catch {
                //Don't care. Stuff is deserialized correctly, even if this fails.
            }
            return ms.ToArray();
        }

        /// <summary>
        /// Return the v4 and the v6 IPs of the local machine (no loopbacks). Use this instead of Dns.GetHostEntry etc. These Dns fxs do not always work. 
        /// </summary>
        public static List<IPAddress> GetIPs() {
            var ips = new List<IPAddress>();
            NetworkInterface[] networkInterfaces = NetworkInterface.GetAllNetworkInterfaces();

            foreach (NetworkInterface network in networkInterfaces) {
                IPInterfaceProperties properties = network.GetIPProperties();

                foreach (IPAddressInformation address in properties.UnicastAddresses) {
                    var ipCandidate = address.Address;
                    if (!ipCandidate.Equals(IPAddress.Loopback) && !ipCandidate.Equals(IPAddress.IPv6Loopback) &&
                       (ipCandidate.AddressFamily == AddressFamily.InterNetwork || ipCandidate.AddressFamily == AddressFamily.InterNetworkV6))
                        ips.Add(ipCandidate);
                }
            }

            return ips;
        }
    }
}
