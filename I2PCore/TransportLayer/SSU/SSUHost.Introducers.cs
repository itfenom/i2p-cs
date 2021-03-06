﻿using System;
using System.Collections.Generic;
using System.Linq;
using I2PCore.SessionLayer;
using I2PCore.TransportLayer.SSU.Data;
using I2PCore.Utils;

namespace I2PCore.TransportLayer.SSU
{
    public partial class SSUHost
    {
        enum GatherIntroducersStates { Startup, Established }

        GatherIntroducersStates GatherIntroducersState = GatherIntroducersStates.Startup;
        PeriodicAction ConsiderUpdateIntroducers = new PeriodicAction( TickSpan.Minutes( 1 ) );

        internal void IntroductionRelayOffered( IntroducerInfo intro )
        {
            if ( !RouterContext.Inst.IsFirewalled )
            {
                return;
            }

            Logging.LogTransport( $"SSU Introduction: Added introducer {intro.Host}, {intro.IntroKey}, {intro.IntroTag}, {intro.EndPoint}" );

            switch ( GatherIntroducersState )
            {
                case GatherIntroducersStates.Startup:
                    ConsiderUpdateIntroducers.Do( () =>
                    {
                        var intros = SelectIntroducers()
                            .Select( p => p.Left.RemoteIntroducerInfo );

                        if ( intros.Any() )
                        {
                            SetIntroducers( intros );
                            ConsiderUpdateIntroducers.Frequency = TickSpan.Minutes( 10 );
                            GatherIntroducersState = GatherIntroducersStates.Established;
                        }
                    } );
                    break;

                case GatherIntroducersStates.Established:
                    ConsiderUpdateIntroducers.Do( () =>
                    {
                        SetIntroducers( SelectIntroducers()
                            .Select( p => p.Left.RemoteIntroducerInfo ) );
                    } );
                    break;
            }
        }

        private IEnumerable<RefPair<SSUSession, EndpointStatistic>> SelectIntroducers()
        {
#if LOG_MUCH_TRANSPORT
            var stats = EPStatisitcs.ToArray().OrderBy( s => s.Value.Score );
            foreach ( var one in stats )
            {
                Logging.LogTransport( one.Value.ToString() );
            }
#endif

            var introsessions = FindSession( s => s.RemoteIntroducerInfo != null );

            var prospects = introsessions
                    .Select( s => new RefPair<SSUSession, EndpointStatistic>(
                        s,
                        EPStatisitcs[s.RemoteEP] ) )
                    .OrderBy( p => p.Right.Score );

            IEnumerable<RefPair<SSUSession, EndpointStatistic>> result;

            if ( prospects.Count() > 10 )
            {
                result = prospects.Take( 3 );
            }
            else
            {
                result = prospects.Take( 2 );
            }

            if ( result.Any() )
            {
                FindSession( s =>
                    s.IsIntroducerConnection = result.Any( r =>
                        r.Left.RemoteEP == s.RemoteEP ) );
            }

            Logging.LogInformation( $"SSUHost: Selected new introducers {string.Join( ", ", result.Select( p => p.Right ) )}" );
            return result;
        }

        internal void IntroducerSessionTerminated( SSUSession s )
        {
            ConsiderUpdateIntroducers.TimeToAction = new TickSpan( 0 );

            Logging.LogTransport( $"Introducer session terminated {s}" );
        }
    }
}
