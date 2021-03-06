﻿using System;
using System.Collections.Generic;
using System.Linq;
using I2PCore.Utils;
using I2PCore.SessionLayer;
using System.Threading;
using System.Net.Sockets;
using System.Net;
using I2PCore.Data;

namespace I2PCore.TransportLayer.NTCP
{
    [TransportProtocol]
    public class NTCPHost: ITransportProtocol
    {
        Thread Worker;
        bool Terminated = false;

        public event Action<ITransport> ConnectionCreated;

        List<NTCPClientIncoming> Clients = new List<NTCPClientIncoming>();

        public NTCPHost()
        {
            RouterContext.Inst.NetworkSettingsChanged += NetworkSettingsChanged;

            UpdateRouterContext();

            Worker = new Thread( Run )
            {
                Name = "NTCPHost",
                IsBackground = true
            };
            Worker.Start();
        }

        void Run()
        {
            try
            {
                while ( !Terminated )
                {
                    var listener = CreateListener();

                    try
                    {
                        listener.BeginAccept( new AsyncCallback( DoAcceptTcpClientCallback ), listener );

                        while ( !Terminated )
                        {
                            Thread.Sleep( 2000 );

                            lock ( Clients )
                            {
                                var terminated = Clients.Where( c => c.Terminated ).ToArray();
                                foreach ( var one in terminated )
                                {
                                    Clients.Remove( one );
                                }
                            }

                            if ( SettingsChanged )
                            {
                                SettingsChanged = false;

                                listener.Shutdown( SocketShutdown.Both );
                                listener.Close();

                                Thread.Sleep( 3000 );

                                listener = CreateListener();
                                listener.BeginAccept( new AsyncCallback( DoAcceptTcpClientCallback ), listener );

                                Logging.LogInformation( "NTCPHost: Running with new network settings. " +
                                    listener.LocalEndPoint.ToString() + ":" + RouterContext.Inst.TCPPort.ToString() + 
                                    " (" + RouterContext.Inst.ExtAddress.ToString() + ")" );
                            }
                        }
                    }
                    catch ( ThreadAbortException ex )
                    {
                        Logging.Log( ex );
                    }
                    catch ( Exception ex )
                    {
                        Logging.Log( ex );
                    }
                }
            }
            finally
            {
                Terminated = true;
                Worker = null;
            }
        }

        private static Socket CreateListener()
        {
            Socket listener;
            listener = new Socket( AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp );
            listener.Bind( new IPEndPoint( RouterContext.Inst.LocalInterface, RouterContext.Inst.TCPPort ) );
            listener.Listen( 20 );
            return listener;
        }

        bool SettingsChanged = false;

        public int BlockedRemoteAddressesCount => 0;

        public void NetworkSettingsChanged()
        {
            SettingsChanged = true;
            UpdateRouterContext();
        }

        private void UpdateRouterContext()
        {
            if ( RouterContext.Inst.IsFirewalled )
            {
                RouterContext.Inst.UpdateAddress( this, null );
            }
            else
            {
                var addr = new I2PRouterAddress( RouterContext.Inst.ExtAddress, RouterContext.Inst.TCPPort, 11, "NTCP" );
                RouterContext.Inst.UpdateAddress( this, addr );
            }
        }

        void DoAcceptTcpClientCallback( IAsyncResult ar )
        {
            if ( !ar.IsCompleted ) return;
            bool docontinue = true;

            var listener = (Socket)ar.AsyncState;

            try
            {
                var socket = listener.EndAccept( ar );

                var ntcpc = new NTCPClientIncoming( socket );
                Logging.LogTransport( $"NTCPHost: incoming connection {ntcpc.DebugId} from " + 
                    $"{socket.RemoteEndPoint} created." );

                ConnectionCreated?.Invoke( ntcpc );

                ntcpc.Connect();
                lock ( Clients )
                {
                    Clients.Add( ntcpc );
                }
            }
            catch ( ObjectDisposedException )
            {
                docontinue = false;
            }
            catch ( Exception ex )
            {
                Logging.Log( ex );
            }

            try
            {
                if ( docontinue ) listener.BeginAccept( new AsyncCallback( DoAcceptTcpClientCallback ), listener );
            }
            catch ( Exception ex )
            {
                Logging.Log( ex );
            }
        }

        public ProtocolCapabilities ContactCapability( I2PRouterInfo router )
        {
            return router.Adresses.Any( ra => ra.TransportStyle == "NTCP" && ra.HaveHostAndPort )
                            ? ProtocolCapabilities.IncomingLowPrio
                            : ProtocolCapabilities.None;
        }

        public ITransport AddSession( I2PRouterInfo router )
        {
            return new NTCPClientOutgoing( router );
        }
    }
}
