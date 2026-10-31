using System;
using System.Collections.Generic;
using Opc.Ua;
using Opc.Ua.Client;

namespace TIAOpennessStudio.Models
{
    /// <summary>
    /// OPC UA subscription info for managing the subscription lifecycle and monitored items.
    /// </summary>
    public class SubscriptionInfo
    {
        /// <summary>
        /// Unique subscription id (GUID format).
        /// </summary>
        public string SubscriptionId { get; set; }

        /// <summary>
        /// The underlying OPC UA subscription object.
        /// </summary>
        public Subscription OpcSubscription { get; set; }

        /// <summary>
        /// Node ids monitored by this subscription.
        /// </summary>
        public List<string> NodeIds { get; set; } = new List<string>();

        /// <summary>
        /// Publishing interval in milliseconds; controls the update rate.
        /// </summary>
        public int PublishingIntervalMs { get; set; }

        /// <summary>
        /// When this subscription was created.
        /// </summary>
        public DateTime CreatedAt { get; set; }
    }
}
