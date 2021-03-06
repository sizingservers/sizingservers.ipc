﻿/*
Original author: Dieter Vandroemme, dev at Sizing Servers Lab (https://www.sizingservers.be) @ University College of West-Flanders, Department GKG
Written in 2015

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
 */

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Serialization.Formatters.Binary;
using System.Threading.Tasks;

namespace SizingServers.IPC {
    /// <summary>
    /// <para>Add a new Sender in the code of the process you want to send messages. Make sure the handles matches the one of the Receivers.</para>
    /// <para>Suscribe to OnSendFailed for error handeling. Please note: Sending will always fail when a Receiver disappears.</para>
    /// </summary>
    public class Sender : IDisposable {
        /// <summary>
        /// 
        /// </summary>
        public event EventHandler<MessageEventArgs> BeforeMessageSent;
        /// <summary>
        /// 
        /// </summary>
        public event EventHandler<MessageEventArgs> AfterMessageSent;
        /// <summary>
        /// 
        /// </summary>
        public event EventHandler<ErrorEventArgs> OnSendFailed;

        private BinaryFormatter _bf;
        private ConcurrentDictionary<TcpClient, IPEndPoint> _tcpSenders;

        private readonly object _lock = new object();

        /// <summary>
        /// Hashcode of the message. When resending the same data it is not serialized again.
        /// </summary>
        private int _hashcode;
        private byte[] _bytes;

        /// <summary>
        /// </summary>
        public bool IsDisposed { get; private set; }
        /// <summary>
        /// 
        /// </summary>
        public string Handle { get; private set; }
        /// <summary>
        /// <para>When true, a message (+ encapsulation) you send is kept in memory. When you resend the same message it will not be serialized again.</para>
        /// </summary>
        public bool Buffered { get; private set; }
        /// <summary>
        /// <para>This is an optional parameter in the constructor.</para>
        /// <para>If you don't use it, receiver end points are stored in the Windows registry and IPC communication is only possible for processes running under the current local user.</para>
        /// <para>If you do use it, these end points are fetched from a Windows service over tcp, making it a distributed IPC.This however will be slower and implies a security risk since there will be network traffic.</para>
        /// </summary>
        public EndPointManagerServiceConnection EndPointManagerServiceConnection { get; private set; }

        /// <summary>
        /// <para>Add a new Sender in the code of the process you want to send messages. Make sure the handles matches the one of the Receivers.</para>
        /// <para>When using the end point manager service, some security measures are advised.</para>
        /// <para>You can use Shared.Encrypt(...) (and Shared.Decrypt(...)) to encrypt messages (if they are strings) before sending them.</para>
        /// <para>Alternatively you can use a ssh tunnel, that will probably be safer and faster</para>
        /// <para>Suscribe to OnSendFailed for error handeling. Please note: Sending will always fail when a Receiver disappears.</para>
        /// </summary>
        /// <param name="handle">
        /// <para>The handle is a value shared by a Sender and its Receivers.  ; , * + and - cannot be used!</para>
        /// <para>It links both parties so messages from a Sender get to the right Receivers.</para>
        /// <para>Make sure this is a unique value: use a GUID for instance:</para>
        /// <para>There is absolutely no checking to see if this handle is used in another Sender - Receivers relation.</para>
        /// </param>
        /// <param name="endPointManagerServiceConnection">
        /// <para>This is an optional parameter.</para>
        /// <para>If you don't use it, receiver end points are stored in the Windows registry and IPC communication is only possible for processes running under the current local user.</para>
        /// <para>If you do use it, these end points are fetched from a Windows service over tcp, making it a distributed IPC.This however will be slower and implies a security risk since there will be network traffic.</para>
        /// </param>
        /// <param name="buffered">
        /// <para>When true, a message (+ encapsulation) you send is kept in memory. When you resend the same message it will not be serialized again.</para>
        /// <para>This buffer can ony hold one message. Using this will make sending messages faster and will take up more memory. Use this wisely for large messages.</para>
        /// </param>
        public Sender(string handle, EndPointManagerServiceConnection endPointManagerServiceConnection = null, bool buffered = false) {
            if (string.IsNullOrWhiteSpace(handle)) throw new ArgumentNullException(handle);
            if (handle.Contains(";") || handle.Contains(",") || handle.Contains("*") || handle.Contains("+") || handle.Contains("-"))
                throw new ArgumentNullException(handle);

            Handle = handle;
            Buffered = buffered;
            EndPointManagerServiceConnection = endPointManagerServiceConnection;

            _tcpSenders = new ConcurrentDictionary<TcpClient, IPEndPoint>();
            _bf = new BinaryFormatter();
        }
        /// <summary>
        /// Send a message to the Receivers. This is (partly) a blocking function.
        /// </summary>
        /// <param name="message">
        /// If the given object is a byte array, it will not be serialized. Otherwise, the object will be serialized using a binary formatter.
        /// </param>
        public void Send(object message) {
            lock (_lock)
                try {
                    if (!IsDisposed) {
                        SetTcpSenders();

                        BeforeMessageSent?.Invoke(this, new MessageEventArgs() { Handle = Handle, Message = message, RemoteEndPoints = _tcpSenders.Values.ToArray() });

                        if (_tcpSenders.Count != 0)
                            if (Buffered) {
                                if (message.GetHashCode() != _hashcode) {
                                    _bytes = SerializeMessage(message);
                                    _hashcode = message.GetHashCode();
                                }
                                Parallel.ForEach(_tcpSenders, (kvp) => Send(kvp.Key, kvp.Value, _bytes));
                            }
                            else {
                                Parallel.ForEach(_tcpSenders, (kvp) => Send(kvp.Key, kvp.Value, SerializeMessage(message)));
                            }

                        AfterMessageSent?.Invoke(this, new MessageEventArgs() { Handle = Handle, Message = message, RemoteEndPoints = _tcpSenders.Values.ToArray() });
                    }
                }
                catch (Exception ex) {
                    if (!IsDisposed && OnSendFailed != null) OnSendFailed(this, new ErrorEventArgs(ex));
                }
        }

        /// <summary>
        /// Writes the handle size, the handle (UTF8 encoding), 1 if message is byte array or 0, the message size and the message to an array.
        /// </summary>
        /// <param name="message"></param>
        /// <returns></returns>
        private byte[] SerializeMessage(object message) {
            bool messageIsByteArray = message is byte[];

            byte[] handleBytes = Shared.GetBytes(Handle);
            byte[] handleSizeBytes = Shared.GetBytes(handleBytes.LongLength);

            byte[] messageBytes = messageIsByteArray ? message as byte[] : Shared.GetBytes(message, _bf);
            byte[] messageSizeBytes = Shared.GetBytes(messageBytes.LongLength);

            var bytes = new byte[handleSizeBytes.LongLength + handleBytes.LongLength + 1 + messageSizeBytes.LongLength + messageBytes.LongLength];

            long pos = 0L;
            handleSizeBytes.CopyTo(bytes, pos);
            pos += handleSizeBytes.LongLength;

            handleBytes.CopyTo(bytes, pos);
            pos += handleBytes.LongLength;

            bytes[pos++] = Shared.GetByte(messageIsByteArray);

            messageSizeBytes.CopyTo(bytes, pos);
            pos += messageSizeBytes.LongLength;

            messageBytes.CopyTo(bytes, pos);

            return bytes;
        }

        /// <summary>
        /// Clean up the stored tcp clients (_senders) and add new ones if need be.
        /// </summary>
        private void SetTcpSenders() {
            var newSenders = new ConcurrentDictionary<TcpClient, IPEndPoint>();
            foreach (IPEndPoint endPoint in EndPointManager.GetReceiverEndPoints(Handle, EndPointManagerServiceConnection)) {
                bool senderFound = false;
                foreach (TcpClient sender in _tcpSenders.Keys)
                    if (_tcpSenders[sender].Equals(endPoint)) {
                        newSenders.TryAdd(sender, endPoint);
                        senderFound = true;
                        break;
                    }

                if (!senderFound)
                    newSenders.TryAdd(new TcpClient(endPoint.AddressFamily), endPoint);
            }

            foreach (TcpClient oldSender in _tcpSenders.Keys) {
                bool equals = false;
                foreach (TcpClient sender in newSenders.Keys)
                    if (_tcpSenders[oldSender].Equals(newSenders[sender])) {
                        equals = true;
                        break;
                    }
                if (!equals)
                    oldSender.Dispose();
            }

            _tcpSenders = newSenders;
        }

        private void Send(TcpClient sender, IPEndPoint endPoint, byte[] bytes) {
            try {
                if (!sender.Connected) {
                    try {
                        _tcpSenders.TryRemove(sender, out endPoint);
                        sender = new TcpClient(endPoint.AddressFamily); //Not always necessary, but ensures that connecting works if the receiver comes back up after it disappeared.
                        _tcpSenders.TryAdd(sender, endPoint);

                        var result = sender.BeginConnect(endPoint.Address, endPoint.Port, null, null);
                        result.AsyncWaitHandle.WaitOne(10000);

                        if (!sender.Connected) {
                            int errorCode = -1;
                            try {
                                errorCode = (int)result.GetType().GetProperty("ErrorCode", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(result);
                            }
                            catch { }

                            throw new Exception("Could not connect to a receiver " + endPoint.Address + ":" + endPoint.Port + 
                                ". Error code " + errorCode + ". Please check the firewall settings of all machines involved and if they are all on the same network.");
                        }

                        sender.EndConnect(result);
                    }
                    catch {
                        //The receiver is not available anymore.
                        _tcpSenders.TryRemove(sender, out endPoint);
                        return;
                    }
                }

                Shared.WriteBytes(sender.GetStream(), sender.SendBufferSize, bytes);

            }
            catch (Exception ex) {
                //The receiver is not available anymore.
                _tcpSenders.TryRemove(sender, out endPoint);

                if (!IsDisposed && OnSendFailed != null) OnSendFailed(this, new ErrorEventArgs(ex));
            }
        }
        /// <summary>
        /// 
        /// </summary>
        public void Dispose() {
            if (!IsDisposed) {
                IsDisposed = true;
                if (_tcpSenders != null) {
                    foreach (TcpClient tcpSender in _tcpSenders.Keys)
                        tcpSender.Dispose();
                    _tcpSenders = null;
                }

                _bf = null;
                Handle = null;
                _bytes = null;
            }
        }
    }
}
