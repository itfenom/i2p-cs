﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using I2PCore.Utils;
using I2P.I2CP.Messages;
using I2PCore;
using I2PCore.Data;
using I2P.I2CP.States;
using I2P.I2CP;
using I2PCore.SessionLayer;
using static I2P.I2CP.Messages.SessionStatusMessage;
using static I2P.I2CP.Messages.MessageStatusMessage;
using static I2PCore.SessionLayer.ClientDestination;
using static I2PCore.Data.I2PSessionConfig;
using static I2P.I2CP.Messages.HostReplyMessage;
using System.Threading;
using static I2P.I2CP.Messages.I2CPMessage;

namespace I2CP.I2CP.States
{
    class HostLookupInfo
    {
        public ushort SessionId;
        public uint RequestId;
    }

    internal class EstablishedState: I2CPState
    {
        internal EstablishedState( I2CPSession sess ) : base( sess ) { }

        internal override I2CPState MessageReceived( I2CPMessage msg )
        {
            switch ( msg )
            {
                case CreateSessionMessage csm:
                    Logging.LogDebug( $"{this}: Received message {csm}." );

                    var signok = I2PSignature.DoVerify(
                            csm.Config.Destination.SigningPublicKey,
                            csm.Config.Signature,
                            csm.Config.SignedBuf );

                    if ( !signok )
                    {
                        Logging.LogDebug( $"{this} CreateSessionMessage: Signature check failed." );
                        Session.Send( new SessionStatusMessage( 0, SessionStates.Invalid ) );
                        return this;
                    }

                    var newdest = Router.CreateDestination(
                         csm.Config.Destination,
                         null,
                         !csm.Config.DontPublishLeaseSet,
                         out var alreadyrunning );

                    if ( alreadyrunning || newdest is null )
                    {
                        Logging.LogDebug( $"{this}: Destination already running." );
                        Session.Send( new SessionStatusMessage( 0, SessionStates.Refused ) );
                        return this;
                    }

                    var newsession = Session.GenerateNewSessionId();
                    newsession.MyDestination = newdest;

                    newsession.Config = csm.Config;
                    UpdateConfiguration( newsession, csm.Config );

                    Session.AttachDestination( newsession.MyDestination );

                    Logging.LogDebug( $"{this}: Creating session {newsession.SessionId}." );

                    var reply = new SessionStatusMessage( newsession.SessionId, SessionStates.Created );
                    Session.Send( reply );

                    Session.SendPendingLeaseUpdates( true );
                    break;

                case ReconfigureSessionMessage rcm:
                    var rcms = Session.SessionIds[rcm.SessionId];
                    rcms.Config = rcm.Config;
                    UpdateConfiguration( rcms, rcms.Config );
                    break;

                case CreateLeaseSetMessage clsm:
                    Logging.LogDebug( $"{this}: {clsm} {clsm.PrivateKey}" );

                    var s = Session.SessionIds[clsm.SessionId];
                    s.PrivateKey = clsm.PrivateKey;

                    if ( s.MyDestination.PrivateKey is null )
                    {
                        s.MyDestination.PrivateKey = clsm.PrivateKey;
                    }

                    s.LeaseInfo = clsm.Info;
                    s.MyDestination.SignedLeases = clsm.Leases;

                    Session.SendPendingLeaseUpdates( true );
                    break;

                case DestLookupMessage dlum:
                    Logging.LogDebug( $"{this}: {dlum} {dlum.Ident.Id32Short}" );

                    Session
                            .SessionIds
                            .First()
                            .Value
                            .MyDestination
                            .LookupDestination( dlum.Ident, HandleDestinationLookupResult, null );
                    break;

                case HostLookupMessage hlum:
                    Logging.LogDebug( $"{this}: {hlum} {hlum.SessionId} {hlum.RequestId} {hlum.Hash?.Id32Short}" );

                    if ( hlum.SessionId == 0xFFFF )
                    {
                        if ( hlum.RequestType == HostLookupMessage.HostLookupTypes.HostName )
                        {
                            Router.LookupDestination(
                                    new I2PIdentHash( hlum.HostName.ToString() ),
                                    HandleHostLookupResult,
                                    new HostLookupInfo { RequestId = hlum.RequestId, SessionId = hlum.SessionId } );
                        }
                        else
                        {
                            Router.LookupDestination(
                                    hlum.Hash,
                                    HandleHostLookupResult,
                                    new HostLookupInfo { RequestId = hlum.RequestId, SessionId = hlum.SessionId } );
                        }
                    }
                    else
                    {
                        var s2 = Session.SessionIds[hlum.SessionId];

                        if ( hlum.RequestType == HostLookupMessage.HostLookupTypes.HostName )
                        {
                            s2.MyDestination.LookupDestination(
                                    new I2PIdentHash( hlum.HostName.ToString() ),
                                    HandleHostLookupResult,
                                    new HostLookupInfo { RequestId = hlum.RequestId, SessionId = hlum.SessionId } );
                        }
                        else
                        {
                            s2.MyDestination.LookupDestination(
                                    hlum.Hash,
                                    HandleHostLookupResult,
                                    new HostLookupInfo { RequestId = hlum.RequestId, SessionId = hlum.SessionId } );
                        }
                    }
                    break;

                case SendMessageMessage smm:
                    Logging.LogDebugData( $"{this}: {smm} {smm.Destination.IdentHash.Id32Short} {smm.Payload}" );

                    SendMessageToDestination(
                            smm.Destination,
                            smm.SessionId,
                            smm.Payload,
                            smm.Nonce );
                    break;

                case SendMessageExpiresMessage smem:
                    Logging.LogDebugData( $"{this}: {smem} {smem.Destination.IdentHash.Id32Short} {(PayloadFormat)smem.Payload[9]} {smem.Payload}" );

                    SendMessageToDestination( 
                            smem.Destination, 
                            smem.SessionId, 
                            smem.Payload, 
                            smem.Nonce );
                    break;

                case DestroySessionMessage dsm:
                    Logging.LogDebug( $"{this}: {dsm}" );
                    Session.Send( new SessionStatusMessage( dsm.SessionId, SessionStates.Destroyed ) );
                    return null;


                default:
                    Logging.LogWarning( $"{this}: Unhandled message {msg}" );
                    break;
            }
            return this;
        }

        private static void UpdateConfiguration( SessionInfo newsession, I2PSessionConfig cfg )
        {
            if ( cfg.Options.Mappings.ContainsKey( new I2PString( "outbound.nickname" ) ) )
            {
                newsession.MyDestination.Name = cfg.Options["outbound.nickname"];
            }

            newsession.MyDestination.TargetInboundTunnelCount = cfg.InboundQuantity;
            newsession.MyDestination.InboundTunnelHopCount = cfg.InboundLength;
            newsession.MyDestination.TargetOutboundTunnelCount = cfg.OutboundQuantity;
            newsession.MyDestination.OutboundTunnelHopCount = cfg.OutboundLength;
        }

        private void SendMessageToDestination( 
                I2PDestination dest, 
                ushort sessid, 
                BufLen payload, 
                uint nonce )
        {
            var s3 = Session.SessionIds[sessid];

            var status = MessageStatatuses.GuaranteedSuccess;
            var sendstaus = s3.MyDestination.Send( dest, payload );

            if ( nonce != 0 )
            {
                switch ( sendstaus )
                {
                    case ClientStates.NoTunnels:
                        status = MessageStatatuses.NoLocalTunnels;
                        break;

                    case ClientStates.NoLeases:
                        status = MessageStatatuses.NoLeaseset;
                        break;
                }

                Session.Send( new MessageStatusMessage(
                        s3.SessionId,
                        s3.MessageId,
                        status, 0, nonce ) );
            }
        }

        private void HandleHostLookupResult( I2PIdentHash hash, I2PLeaseSet ls, object o )
        {
            if ( Session.Terminated ) return;

            var hlinfo = (HostLookupInfo)o;

            Logging.LogDebug( $"{this} HandleDestinationLookupResult: {hlinfo.SessionId} {hlinfo.RequestId} {hash.Id32Short} '{ls}'" );

            if ( ls != null )
            {
                Session.Send( new HostReplyMessage( 
                        hlinfo.SessionId, 
                        hlinfo.RequestId, 
                        ls.Destination ) );
                return;
            }

            Session.Send( new HostReplyMessage(
                        hlinfo.SessionId,
                        hlinfo.RequestId,
                        HostLookupResults.Failure ) );
        }

        void HandleDestinationLookupResult( I2PIdentHash hash, I2PLeaseSet ls, object o )
        {
            if ( Session.Terminated ) return;

            Logging.LogDebug( $"{this} HandleDestinationLookupResult: {hash.Id32Short} '{ls}'" );

            if ( ls != null )
            {
                Session.Send( new DestReplyMessage( ls.Destination ) );
                return;
            }

            Session.Send( new DestReplyMessage( hash ) );
        }
    }
}
