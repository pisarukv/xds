﻿using System.Collections.Generic;
using System.Linq;
using System.Net;
using Microsoft.Extensions.Logging;
using UnnamedCoin.Bitcoin.P2P;
using UnnamedCoin.Bitcoin.Utilities;

namespace UnnamedCoin.Bitcoin.Connection
{
    /// <summary>
    ///     Contract for network peer banning provider.
    /// </summary>
    /// <remarks>
    ///     Peers are banned for <see cref="ConnectionManagerSettings.DefaultMisbehavingBantimeSeconds" /> seconds (default is
    ///     24h), this value can change using configuration (-bantime=[seconds]).
    /// </remarks>
    public interface IPeerBanning
    {
        /// <summary>
        ///     Bans and disconnects the peer.
        /// </summary>
        /// <param name="endpoint">The endpoint to set that it was banned.</param>
        /// <param name="banTimeSeconds">The time in seconds this peer should be banned.</param>
        /// <param name="reason">An optional reason for the ban, the 'reason' is only use for tracing.</param>
        void BanAndDisconnectPeer(IPEndPoint endpoint, int banTimeSeconds, string reason = null);

        /// <summary>
        ///     Bans and disconnects the peer using the connection manager's default ban interval.
        ///     This allows features to depend solely on the peer banning interface and not the connection manager directly.
        /// </summary>
        /// <param name="endpoint">The endpoint to set that it was banned.</param>
        /// <param name="reason">An optional reason for the ban, the 'reason' is only use for tracing.</param>
        void BanAndDisconnectPeer(IPEndPoint endpoint, string reason = null);

        /// <summary>
        ///     Clears the node's banned peer list.
        /// </summary>
        void ClearBannedPeers();

        /// <summary>
        ///     Returns a list of all the banned peers.
        /// </summary>
        List<PeerAddress> GetAllBanned();

        /// <summary>
        ///     Check if a peer is banned.
        /// </summary>
        /// <param name="endpoint">The endpoint to check if it was banned.</param>
        /// <returns><c>true</c> if the peer was banned.</returns>
        bool IsBanned(IPEndPoint endpoint);

        /// <summary>
        ///     Un-bans a banned peer.
        /// </summary>
        /// <param name="endpoint">The endpoint of the peer to un-ban.</param>
        void UnBanPeer(IPEndPoint endpoint);
    }

    /// <summary>
    ///     An implementation of<see cref="IPeerBanning" />.
    ///     This will manage banning of peers and checking for banned peers.
    /// </summary>
    public class PeerBanning : IPeerBanning
    {
        /// <summary>A connection manager of peers.</summary>
        readonly IConnectionManager connectionManager;

        /// <summary>Functionality of date and time.</summary>
        readonly IDateTimeProvider dateTimeProvider;

        /// <summary>Instance logger.</summary>
        readonly ILogger logger;

        /// <summary>Keeps a set of peers discovered on the network in cache and on disk.</summary>
        readonly IPeerAddressManager peerAddressManager;

        public PeerBanning(IConnectionManager connectionManager, ILoggerFactory loggerFactory,
            IDateTimeProvider dateTimeProvider, IPeerAddressManager peerAddressManager)
        {
            this.logger = loggerFactory.CreateLogger(GetType().FullName);
            this.connectionManager = connectionManager;
            this.dateTimeProvider = dateTimeProvider;
            this.peerAddressManager = peerAddressManager;
        }

        /// <inheritdoc />
        public void BanAndDisconnectPeer(IPEndPoint endpoint, int banTimeSeconds, string reason = null)
        {
            Guard.NotNull(endpoint, nameof(endpoint));

            if (banTimeSeconds < 0)
            {
                this.logger.LogTrace("(-)[NO_BAN]");
                return;
            }

            reason = reason ?? "unknown";

            // Find all connected peers from the same IP and disconnect them.
            var peers = this.connectionManager.ConnectedPeers.FindByIp(endpoint.Address);
            foreach (var peer in peers)
            {
                var peerBehavior = peer.Behavior<IConnectionManagerBehavior>();
                if (peerBehavior.Whitelisted)
                {
                    this.logger.LogTrace("(-)[WHITELISTED]");
                    return;
                }

                peer.Disconnect($"The peer was banned, reason: {reason}");
            }

            // Find all peers from the same IP and ban them.
            var peerAddresses = this.peerAddressManager.FindPeersByIp(endpoint);
            if (peerAddresses.Count == 0)
            {
                var address = this.peerAddressManager.AddPeer(endpoint, IPAddress.Loopback);

                // The address could not have been added to the address manager due to it being routable.
                if (address != null)
                {
                    peerAddresses.Add(address);
                    this.logger.LogDebug("{0} added to the address manager.");
                }
            }

            foreach (var peerAddress in peerAddresses)
            {
                peerAddress.BanTimeStamp = this.dateTimeProvider.GetUtcNow();
                peerAddress.BanUntil = this.dateTimeProvider.GetUtcNow().AddSeconds(banTimeSeconds == 0
                    ? this.connectionManager.ConnectionSettings.BanTimeSeconds
                    : banTimeSeconds);
                peerAddress.BanReason = reason;

                this.logger.LogDebug("Peer '{0}' banned for reason '{1}', until '{2}'.", endpoint, reason,
                    peerAddress.BanUntil.ToString());
            }
        }

        /// <inheritdoc />
        public void BanAndDisconnectPeer(IPEndPoint endpoint, string reason = null)
        {
            BanAndDisconnectPeer(endpoint, this.connectionManager.ConnectionSettings.BanTimeSeconds, reason);
        }

        /// <inheritdoc />
        public void ClearBannedPeers()
        {
            foreach (var peer in this.peerAddressManager.Peers)
                if (IsBanned(peer.Endpoint))
                {
                    peer.UnBan();
                    this.logger.LogDebug("Peer '{0}' was un-banned.", peer.Endpoint);
                }
        }

        /// <inheritdoc />
        public List<PeerAddress> GetAllBanned()
        {
            return this.peerAddressManager.Peers.Where(p => p.IsBanned(this.dateTimeProvider.GetUtcNow()))
                .Select(p => p).ToList();
        }

        /// <inheritdoc />
        public bool IsBanned(IPEndPoint endpoint)
        {
            Guard.NotNull(endpoint, nameof(endpoint));

            var peerAddresses = this.peerAddressManager.FindPeersByIp(endpoint);

            if (peerAddresses.Count == 0)
            {
                this.logger.LogTrace("(-)[PEERNOTFOUND]");
                return false;
            }

            return peerAddresses.Any(p => p.IsBanned(this.dateTimeProvider.GetUtcNow()));
        }

        /// <inheritdoc />
        public void UnBanPeer(IPEndPoint endpoint)
        {
            // Find all peers from the same IP and un-ban them.
            var peerAddresses = this.peerAddressManager.FindPeersByIp(endpoint);
            if (peerAddresses.Count == 0)
            {
                this.logger.LogTrace("(-)[NO_PEERS_TO_UNBAN]");
                return;
            }

            foreach (var peerAddress in peerAddresses)
            {
                peerAddress.UnBan();
                this.logger.LogDebug("Peer '{0}' was un-banned.", endpoint);
            }
        }
    }
}