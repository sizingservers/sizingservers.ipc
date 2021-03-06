﻿/*
Original author: Dieter Vandroemme, dev at Sizing Servers Lab (https://www.sizingservers.be) @ University College of West-Flanders, Department GKG
Written in 2016

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
 */

using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;

namespace SizingServers.IPC {
    /// <summary>
    /// </summary>
    public class EndPointManagerServiceConnection {
        private TcpClient _client;

        /// <summary>
        /// End point info to connect to the service over tcp.
        /// </summary>
        public IPEndPoint EndPointManagerServiceEP { get; private set; }

        /// <summary>
        /// [optional] Password for Rijndael encryption for communication with the epm service. Alternatively you can use an ssh tunnel, that will probably be safer and faster.
        /// </summary>
        public string Password { get; private set; }
        /// <summary>
        /// [optional] Salt for Rijndael encryption for communication with the epm service. Example (don't use this): new byte[] { 0x59, 0x06, 0x59, 0x3e, 0x21, 0x4e, 0x55, 0x34, 0x96, 0x15, 0x11, 0x13, 0x72 }
        /// </summary>
        public byte[] Salt { get; private set; }

        /// <summary>
        /// </summary>
        /// <param name="endPointManagerServiceEP">End point info to connect to the service over tcp.</param>
        public EndPointManagerServiceConnection(IPEndPoint endPointManagerServiceEP) {
            EndPointManagerServiceEP = endPointManagerServiceEP;
        }
        /// <summary>
        /// </summary>
        /// <param name="endPointManagerServiceEP">End point info to connect to the service over tcp.</param>
        /// <param name="password">Password for Rijndael encryption for communication with the epm service. Alternatively you can use an ssh tunnel, that will probably be safer and faster.</param>
        /// <param name="salt">Salt for Rijndael encryption for communication with the epm service. Example (don't use this): new byte[] { 0x59, 0x06, 0x59, 0x3e, 0x21, 0x4e, 0x55, 0x34, 0x96, 0x15, 0x11, 0x13, 0x72 }</param>
        public EndPointManagerServiceConnection(IPEndPoint endPointManagerServiceEP, string password, byte[] salt)
            : this(endPointManagerServiceEP) {
            if (password == null || salt == null) throw new ArgumentNullException("password or salt cannot be null.");
            Password = password;
            Salt = salt;
        }

        private TcpClient GetClient() {
            if (_client == null || !_client.Connected) {
                _client = new TcpClient(EndPointManagerServiceEP.AddressFamily);
                var result = _client.BeginConnect(EndPointManagerServiceEP.Address, EndPointManagerServiceEP.Port, null, null);

                result.AsyncWaitHandle.WaitOne(10000);

                if (!_client.Connected) {
                    int errorCode = -1;
                    try {
                        errorCode = (int)result.GetType().GetProperty("ErrorCode", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(result);
                    }
                    catch { }

                    throw new EndPointManagerServiceConnectionException("Could not connect to the end point manager service " + EndPointManagerServiceEP.Address + ":" + EndPointManagerServiceEP.Port +
                        ". Error code " + errorCode + ". Please check the firewall settings of all machines involved and if they are all on the same network.");
                }

                _client.EndConnect(result);
            }

            return _client;
        }


        /// <summary>
        /// Get the endpoints from the endpoint manager service.
        /// </summary>
        /// <param name="message">Empty string to get the end points or the end point represeted as a string to set them.</param>
        /// <returns></returns>
        internal string SendAndReceiveEPM(string message) {
            var client = GetClient();
            //Serialize message
            byte[] messageBytes = MessageToBytes(message);
            byte[] messageSizeBytes = Shared.GetBytes(messageBytes.LongLength);
            byte[] bytes = new byte[messageSizeBytes.LongLength + messageBytes.LongLength];

            long pos = 0L;
            messageSizeBytes.CopyTo(bytes, pos);
            pos += messageSizeBytes.LongLength;
            messageBytes.CopyTo(bytes, pos);

            Stream str = client.GetStream();

            Shared.WriteBytes(str, client.SendBufferSize, bytes);

            long messageSize = Shared.GetLong(Shared.ReadBytes(str, client.ReceiveBufferSize, Shared.LONGSIZE));

            return MessageFromBytes(Shared.ReadBytes(str, client.ReceiveBufferSize, messageSize));
        }
        private string MessageFromBytes(byte[] messageBytes) {
            string message = Shared.GetString(Shared.Ungzip(messageBytes));
            if (Password != null) message = Shared.Decrypt(message, Password, Salt);
            return message;
        }
        private byte[] MessageToBytes(string message) {
            if (Password != null) message = Shared.Encrypt(message, Password, Salt);
            return Shared.Gzip(Shared.GetBytes(message));
        }

    }
}
