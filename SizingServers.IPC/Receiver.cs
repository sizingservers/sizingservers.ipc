﻿/*
Original author: Dieter Vandroemme, dev at Sizing Servers Lab (https://www.sizingservers.be) @ University College of West-Flanders, Department GKG
Written in 2015

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
 */

using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Serialization.Formatters.Binary;
using System.Threading;

namespace SizingServers.IPC {
    /// <summary>
    /// <para>Add a new Receiver in the code of the process you want to receive messages. Make sure the handles matches the one of the Sender.</para>
    /// </summary>
    public class Receiver : IDisposable {
        /// <summary>
        /// 
        /// </summary>
        public event EventHandler<MessageEventArgs> MessageReceived;

        private BinaryFormatter _bf;
        private TcpListener _tcpReceiver;

        /// <summary>
        /// 
        /// </summary>
        public bool IsDisposed { get; private set; }
        /// <summary>
        /// <para>The handle is a value shared by a Sender and its Receivers.</para>
        /// <para>It links both parties so messages from a Sender get to the right Receivers.</para>
        /// <para>Make sure this is a unique value: use a GUID for instance:</para>
        /// <para>There is absolutely no checking to see if this handle is used in another Sender - Receivers relation.</para>
        /// </summary>
        public string Handle { get; private set; }

        /// <summary>
        /// The end point this receiver is listening on.
        /// </summary>
        public EndPoint LocalEndPoint { get { return _tcpReceiver?.LocalEndpoint; } }
        /// <summary>
        /// <para>This is an optional parameter in the constructor.</para>
        /// <para>If you don't use it, receiver end points are stored in the Windows registry and IPC communication is only possible for processes running under the current local user.</para>
        /// <para>If you do use it, these end points are fetched from a Windows service over tcp, making it a distributed IPC.This however will be slower and implies a security risk since there will be network traffic.</para>
        /// </summary>
        public EndPointManagerServiceConnection EndPointManagerServiceConnection { get; private set; }

        /// <summary>
        /// <para>Receives messages of a Sender having the same handle.</para>
        /// <para>When using the end point manager service, some security measures are advised.</para>
        /// <para>You can use Shared.Encrypt(...) (and Shared.Decrypt(...)) to encrypt your received messages (if they are strings) via the MessageReceived event.</para>
        /// <para>Alternatively you can use a ssh tunnel, that will probably be safer and faster</para>
        /// </summary>
        /// <param name="handle">
        /// <para>The handle is a value shared by a Sender and its Receivers.  ; , * + and - cannot be used!</para>
        /// <para>It links both parties so messages from a Sender get to the right Receivers.</para>
        /// <para>Make sure this is a unique value: use a GUID for instance:</para>
        /// <para>There is absolutely no checking to see if this handle is used in another Sender - Receivers relation.</para>
        /// </param>
        /// <param name="ipAddressToRegister">
        /// <para>This parameter is only useful if you are using an end point manager service.</para>
        /// <para>A receiver listens to all available IPs for connections. The ip that is registered on the end point manager (service) is by default automatically determined.</para>
        /// <para>However, this does not take into account that senders, receiver or end point manager services are possibly not on the same network.</para>
        /// <para>Therefor you can override this behaviour by supplying your own IP that will be registered to the end point manager service.</para>
        /// </param>
        /// <param name="allowedPorts">
        /// <para>This parameter is only useful if you are using an end point manager service.</para>
        /// <para>To make firewall settings easier, you can specify a pool of TCP ports where the receiver can choose one from to listen on. If none of these ports are available, this will fail.</para>
        /// <para>If you don't use this parameter, one of the total available ports on the system will be chosen.</para>
        /// </param>
        /// <param name="endPointManagerServiceConnection">
        /// <para>This is an optional parameter.</para>
        /// <para>If you don't use it, receiver end points are stored in the Windows registry and IPC communication is only possible for processes running under the current local user.</para>
        /// <para>If you do use it, these end points are fetched from a Windows service over tcp, making it a distributed IPC.This however will be slower and implies a security risk since there will be network traffic.</para>
        /// </param>
        public Receiver(string handle, IPAddress ipAddressToRegister = null, int[] allowedPorts = null, EndPointManagerServiceConnection endPointManagerServiceConnection = null) {
            if (string.IsNullOrWhiteSpace(handle)) throw new ArgumentNullException(handle);
            if (handle.Contains(";") || handle.Contains(",") || handle.Contains("*") || handle.Contains("+") || handle.Contains("-"))
                throw new ArgumentNullException(handle);

            Handle = handle;
            EndPointManagerServiceConnection = endPointManagerServiceConnection;

            for (int i = 1; i != 21; i++) //Try 20 times, for when the same port is chosen by another application.
                try {
                    _tcpReceiver = new TcpListener(EndPointManager.RegisterReceiver(Handle, ipAddressToRegister, allowedPorts, EndPointManagerServiceConnection));
                    _tcpReceiver.Start(endPointManagerServiceConnection == null ? 1 : 2); //Keep one connection open to enable the service pinging it.
                    break;
                }
                catch (EndPointManagerServiceConnectionException) {
                    throw;
                }
                catch (Exception) {
                    //Not important. If it doesn't work the sender does not exist anymore or the sender will handle it.
                    Thread.Sleep(i * 10);
                }

            _bf = new BinaryFormatter();

            BeginReceive();
        }

        private void BeginReceive() {
            ThreadPool.QueueUserWorkItem((state) => {
                while (!IsDisposed)
                    if (MessageReceived != null) {
                        try {
                            HandleReceive(_tcpReceiver.AcceptTcpClient());
                        }
                        catch {
                            if (!IsDisposed)
                                throw;
                        }
                    }
                    else {
                        Thread.Sleep(1);
                    }
            }, null);
        }

        /// <summary>
        /// <para>Reads handle size, handle, 1 if message is byte array or 0, message size and message from the stream.</para>
        /// <para>If the handle in the message is invalid the connection will be closed.</para>
        /// </summary>
        /// <param name="client"></param>
        private void HandleReceive(TcpClient client) {
            ThreadPool.QueueUserWorkItem((state) => {
                try {
                    while (!IsDisposed && client != null && client.Connected) {
                        Stream str = client.GetStream();

                        long handleSize = Shared.GetLong(Shared.ReadBytes(str, client.ReceiveBufferSize, Shared.LONGSIZE));
                        string handle = Shared.GetString(Shared.ReadBytes(str, client.ReceiveBufferSize, handleSize));

                        if (handle == Handle) {
                            bool messageIsByteArray = Shared.GetBool(Shared.ReadBytes(str, client.ReceiveBufferSize, 1));
                            long messageSize = Shared.GetLong(Shared.ReadBytes(str, client.ReceiveBufferSize, Shared.LONGSIZE));

                            byte[] messageBytes = Shared.ReadBytes(str, client.ReceiveBufferSize, messageSize);

                            object message = messageIsByteArray ? messageBytes : Shared.GetObject(messageBytes, _bf);

                            MessageReceived?.Invoke(this, new MessageEventArgs() { Handle = Handle, Message = message });
                        }
                        else {
                            //Invalid sender or ping from EPM service. Close the connection.
                            client.Dispose();
                        }
                    }
                }
                catch {
                    //Not important. If it doesn't work the sender does not exist anymore or the sender will handle it.
                }
            }, null);
        }

        /// <summary>
        /// 
        /// </summary>
        public void Dispose() {
            if (!IsDisposed) {
                IsDisposed = true;

                if (_tcpReceiver != null) {
                    _tcpReceiver.Stop();
                    _tcpReceiver = null;
                }
                _bf = null;

                Handle = null;
            }
        }
    }
}
