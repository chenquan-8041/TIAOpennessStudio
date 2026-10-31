using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Client;
using TIAOpennessStudio.Models;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAOpennessStudio.Services.Implementations
{
    public class OpcUaService : IOpcUaService, IDisposable
    {
        private readonly ILogger<OpcUaService> _logger;
        private Session _session;
        private OpcUaConnectionConfig _config;
        private OpcUaConnectionState _state = OpcUaConnectionState.Disconnected;
        private readonly Dictionary<string, SubscriptionInfo> _subscriptions = new Dictionary<string, SubscriptionInfo>();
        private Timer _reconnectTimer;

        public event Action<OpcUaConnectionState> ConnectionStateChanged;
        public event OpcUaDataChangedHandler DataChanged;

        public OpcUaConnectionState State => _state;
        public bool IsConnected => _session != null && _session.Connected;

        // Write-enable safety gate: writing to a running PLC is blocked by default until the user enables it explicitly.
        public bool WriteEnabled { get; set; } = false;

        public OpcUaService(ILogger<OpcUaService> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        private void SetState(OpcUaConnectionState s)
        {
            _state = s;
            ConnectionStateChanged?.Invoke(s);
        }

        /// <summary>
        /// Replaces the host+port of the server-returned discoveredUrl with the
        /// host+port of the URL the user actually dialed (requestedUrl), while
        /// keeping discoveredUrl's scheme and path. Fixes the case where a server
        /// returns an unrouteable hostname in GetEndpoints.
        /// </summary>
        private static string ReplaceHostPreservePath(string discoveredUrl, string requestedUrl)
        {
            if (string.IsNullOrEmpty(discoveredUrl) || string.IsNullOrEmpty(requestedUrl))
                return discoveredUrl;

            try
            {
                var discovered = new Uri(discoveredUrl);
                var requested = new Uri(requestedUrl);

                // Already the same host+port; no replacement needed
                if (string.Equals(discovered.Host, requested.Host, StringComparison.OrdinalIgnoreCase)
                    && discovered.Port == requested.Port)
                {
                    return discoveredUrl;
                }

                var builder = new UriBuilder(discovered)
                {
                    Host = requested.Host,
                    Port = requested.Port
                };
                return builder.Uri.ToString().TrimEnd('/');
            }
            catch
            {
                // On parse failure fall back to the requested URL, at least ensuring a reachable address
                return requestedUrl;
            }
        }

        /// <summary>
        /// When discovery (GetEndpoints) fails, manually build an endpoint
        /// description with SecurityPolicy=None / MessageSecurityMode=None and
        /// connect unencrypted using the user-supplied URL directly, bypassing the
        /// broken discovery endpoint.
        /// </summary>
        private static EndpointDescription BuildManualNoneEndpoint(string endpointUrl)
        {
            return new EndpointDescription
            {
                EndpointUrl = endpointUrl,
                Server = new ApplicationDescription
                {
                    ApplicationUri = endpointUrl,
                    ApplicationName = "OPC UA Server",
                    ApplicationType = ApplicationType.Server
                },
                SecurityMode = MessageSecurityMode.None,
                SecurityPolicyUri = SecurityPolicies.None,
                TransportProfileUri = Profiles.UaTcpTransport,
                UserIdentityTokens = new UserTokenPolicyCollection
                {
                    new UserTokenPolicy(UserTokenType.Anonymous),
                    new UserTokenPolicy(UserTokenType.UserName)
                }
            };
        }

        public async Task<bool> ConnectAsync(OpcUaConnectionConfig config, CancellationToken ct = default)
        {
            _logger.LogInformation("Connecting to OPC UA server at {EndpointUrl}", config.EndpointUrl);
            _config = config;
            SetState(OpcUaConnectionState.Connecting);

            try
            {
                var appConfig = new ApplicationConfiguration
                {
                    ApplicationName = "TIAOpennessStudio",
                    ApplicationType = ApplicationType.Client,
                    SecurityConfiguration = new SecurityConfiguration
                    {
                        AutoAcceptUntrustedCertificates = true,
                        ApplicationCertificate = new CertificateIdentifier
                        {
                            StoreType = "Directory",
                            StorePath = "%CommonApplicationData%\\OPC Foundation\\CertificateStores\\MachineDefault"
                        },
                        TrustedIssuerCertificates = new CertificateTrustList
                        {
                            StoreType = "Directory",
                            StorePath = "%CommonApplicationData%\\OPC Foundation\\CertificateStores\\UA Certificate Authorities"
                        },
                        TrustedPeerCertificates = new CertificateTrustList
                        {
                            StoreType = "Directory",
                            StorePath = "%CommonApplicationData%\\OPC Foundation\\CertificateStores\\UA Applications"
                        },
                        RejectedCertificateStore = new CertificateTrustList
                        {
                            StoreType = "Directory",
                            StorePath = "%CommonApplicationData%\\OPC Foundation\\CertificateStores\\RejectedCertificates"
                        }
                    },
                    // Transport quotas: the defaults are too small for the handshake on some
                    // servers (e.g. S7-1500), and a too-short discovery timeout ends in
                    // BadNotConnected. Enlarge buffer/message sizes and extend the channel
                    // lifetime and operation timeout here.
                    TransportQuotas = new TransportQuotas
                    {
                        OperationTimeout = config.ReadTimeoutMs > 0 ? Math.Max(config.ReadTimeoutMs, 15000) : 15000,
                        MaxStringLength = 1048576,
                        MaxByteStringLength = 4194304,
                        MaxArrayLength = 65535,
                        MaxMessageSize = 4194304,
                        MaxBufferSize = 65535,
                        ChannelLifetime = 300000,
                        SecurityTokenLifetime = 3600000
                    },
                    ClientConfiguration = new ClientConfiguration
                    {
                        DefaultSessionTimeout = 60000
                    }
                };
                await appConfig.Validate(ApplicationType.Client);

                // Security channel is needed when either SecurityPolicy or MessageSecurityMode is non-None.
                bool useSecurity =
                    (!string.IsNullOrEmpty(config.SecurityPolicy) &&
                     !config.SecurityPolicy.Equals("None", StringComparison.OrdinalIgnoreCase))
                    ||
                    (!string.IsNullOrEmpty(config.MessageSecurityMode) &&
                     !config.MessageSecurityMode.Equals("None", StringComparison.OrdinalIgnoreCase));

                // Discovery timeout (ms): allow enough time so a slightly slow PLC reply doesn't end in BadNotConnected.
                int discoverTimeout = config.ReadTimeoutMs > 0 ? Math.Max(config.ReadTimeoutMs, 15000) : 15000;

                EndpointDescription endpoint;
                try
                {
                    // Preferred: pick an endpoint matching the security settings via the server discovery service (GetEndpoints).
                    endpoint = await Task.Run(() =>
                        CoreClientUtils.SelectEndpoint(config.EndpointUrl, useSecurity, discoverTimeout));

                    // Key fix: many servers (e.g. the S7-1500 OPC UA server) overwrite EndpointUrl
                    // in the GetEndpoints response with their "self-perceived" hostname/IP
                    // (e.g. opc.tcp://PLCNAME:4840), which is often unresolvable/unrouteable
                    // from the client — so TCP connects to the real IP but the OPC UA secure
                    // channel fails. Replace the discovered endpoint's host with the address the
                    // user actually dialed.
                    endpoint.EndpointUrl = ReplaceHostPreservePath(endpoint.EndpointUrl, config.EndpointUrl);
                }
                catch (Exception discoverEx)
                {
                    // Discovery failed (commonly BadNotConnected): the TCP port connects (telnet
                    // works) but the GetEndpoints handshake fails. Fall back to a manually
                    // built None/None endpoint and connect directly using the user-supplied URL.
                    _logger.LogWarning(discoverEx,
                        "SelectEndpoint (discovery) failed for {EndpointUrl}, falling back to manual None endpoint",
                        config.EndpointUrl);

                    if (useSecurity)
                    {
                        // User asked for encryption but even discovery failed, so we cannot guess a security policy — rethrow the original error.
                        throw;
                    }

                    endpoint = BuildManualNoneEndpoint(config.EndpointUrl);
                }

                _logger.LogInformation(
                    "Selected endpoint: {EndpointUrl}, SecurityPolicy={Policy}, SecurityMode={Mode}",
                    endpoint.EndpointUrl, endpoint.SecurityPolicyUri, endpoint.SecurityMode);

                var configuredEndpoint = new ConfiguredEndpoint(null, endpoint, EndpointConfiguration.Create(appConfig));

                _session = await Task.Run(() =>
                    Session.Create(appConfig, configuredEndpoint, false, "TIAOpennessStudio Session", 60000,
                        !string.IsNullOrEmpty(config.Username)
                            ? new UserIdentity(config.Username, config.Password ?? string.Empty)
                            : new UserIdentity(),
                        null));

                _session.KeepAlive += OnKeepAlive;

                if (config.AutoReconnect)
                {
                    ScheduleReconnect();
                }

                SetState(OpcUaConnectionState.Connected);
                _logger.LogInformation("Successfully connected to OPC UA server at {EndpointUrl}", config.EndpointUrl);
                return true;
            }
            catch (ServiceResultException sre)
            {
                _logger.LogError(sre,
                    "Failed to connect to OPC UA server at {EndpointUrl}. StatusCode={StatusCode} ({StatusText})",
                    config.EndpointUrl, sre.StatusCode, sre.Message);
                SetState(OpcUaConnectionState.Disconnected);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to connect to OPC UA server at {EndpointUrl}", config.EndpointUrl);
                SetState(OpcUaConnectionState.Disconnected);
                throw;
            }
        }

        private void OnKeepAlive(ISession session, KeepAliveEventArgs e)
        {
            if (ServiceResult.IsBad(e.Status))
            {
                SetState(OpcUaConnectionState.Reconnecting);
            }
        }

        private void ScheduleReconnect()
        {
            _reconnectTimer?.Dispose();
            var interval = _config?.ReconnectIntervalMs ?? 5000;
            _reconnectTimer = new Timer(_ =>
            {
                if (_session == null || _session.Connected) return;
                try
                {
                    SetState(OpcUaConnectionState.Reconnecting);
                    _session.Reconnect();
                    SetState(OpcUaConnectionState.Connected);
                    
                    // Auto-restore subscriptions after reconnection (Requirement 3.15, 3.16, NFR8)
                    if (_config.AutoReconnect && _subscriptions.Count > 0)
                    {
                        _logger.LogInformation("Restoring {SubscriptionCount} subscriptions after reconnection", _subscriptions.Count);
                        
                        // Create a copy to avoid modification during iteration
                        var subscriptionsToRestore = new List<SubscriptionInfo>(_subscriptions.Values);
                        
                        foreach (var subscriptionInfo in subscriptionsToRestore)
                        {
                            try
                            {
                                // Re-create subscription with same settings
                                var subscription = new Subscription(_session.DefaultSubscription)
                                {
                                    PublishingInterval = subscriptionInfo.PublishingIntervalMs,
                                    PublishingEnabled = true,
                                    LifetimeCount = 10000,
                                    KeepAliveCount = 100,
                                    MaxNotificationsPerPublish = 1000
                                };

                                // Re-create monitored items
                                var monitoredItems = subscriptionInfo.NodeIds.Select(nodeIdStr =>
                                {
                                    var monitoredItem = new MonitoredItem(subscription.DefaultItem)
                                    {
                                        DisplayName = nodeIdStr,
                                        StartNodeId = new NodeId(nodeIdStr),
                                        AttributeId = Attributes.Value,
                                        SamplingInterval = subscriptionInfo.PublishingIntervalMs,
                                        QueueSize = 10,
                                        DiscardOldest = true
                                    };
                                    
                                    monitoredItem.Notification += OnMonitoredItemNotification;
                                    return monitoredItem;
                                }).ToList();

                                subscription.AddItems(monitoredItems);
                                _session.AddSubscription(subscription);
                                subscription.Create();
                                
                                // Update subscription info with new OPC subscription object
                                subscriptionInfo.OpcSubscription = subscription;
                                
                                _logger.LogInformation("Restored subscription {SubscriptionId} with {NodeCount} nodes", 
                                    subscriptionInfo.SubscriptionId, subscriptionInfo.NodeIds.Count);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Failed to restore subscription {SubscriptionId}", 
                                    subscriptionInfo.SubscriptionId);
                            }
                        }
                    }
                }
                catch
                {
                    // will retry on next interval
                }
            }, null, interval, interval);
        }

        public Task DisconnectAsync(CancellationToken ct = default)
        {
            _logger.LogInformation("Disconnecting from OPC UA server");
            _reconnectTimer?.Dispose();
            _reconnectTimer = null;

            foreach (var subscriptionInfo in _subscriptions.Values)
            {
                try { _session?.RemoveSubscription(subscriptionInfo.OpcSubscription); } catch { }
            }
            _subscriptions.Clear();

            if (_session != null)
            {
                _session.KeepAlive -= OnKeepAlive;
                try { _session.Close(); } catch { }
                _session.Dispose();
                _session = null;
            }

            SetState(OpcUaConnectionState.Disconnected);
            _logger.LogInformation("Disconnected from OPC UA server");
            return Task.CompletedTask;
        }

        private void EnsureConnected()
        {
            if (_session == null || !_session.Connected)
                throw new InvalidOperationException("OPC UA Session not connected");
        }

        public async Task<OpcUaValueSnapshot> ReadNodeAsync(string nodeId, CancellationToken ct = default)
        {
            EnsureConnected();
            var id = new NodeId(nodeId);

            var node = await Task.Run(() => _session.ReadNode(id));
            var value = await Task.Run(() => _session.ReadValue(id));

            return new OpcUaValueSnapshot
            {
                NodeId = nodeId,
                DisplayName = node.DisplayName?.Text ?? nodeId,
                Value = value.Value,
                DataType = value.WrappedValue.TypeInfo?.BuiltInType.ToString(),
                Quality = MapQuality(value.StatusCode),
                SourceTimestamp = value.SourceTimestamp != DateTime.MinValue ? value.SourceTimestamp : (DateTime?)null,
                ServerTimestamp = value.ServerTimestamp != DateTime.MinValue ? value.ServerTimestamp : (DateTime?)null
            };
        }

        public async Task<IReadOnlyList<OpcUaValueSnapshot>> ReadNodesAsync(IEnumerable<string> nodeIds, CancellationToken ct = default)
        {
            EnsureConnected();
            var ids = nodeIds.Select(n => new NodeId(n)).ToList();
            var nodesToRead = new ReadValueIdCollection(
                ids.Select(id => new ReadValueId { NodeId = id, AttributeId = Attributes.Value }));

            var results = await Task.Run(() =>
            {
                _session.Read(null, 0, TimestampsToReturn.Both, nodesToRead,
                    out DataValueCollection values, out DiagnosticInfoCollection _);
                return values;
            });

            var snapshots = new List<OpcUaValueSnapshot>();
            for (int i = 0; i < ids.Count; i++)
            {
                var dv = results[i];
                snapshots.Add(new OpcUaValueSnapshot
                {
                    NodeId = ids[i].ToString(),
                    Value = dv.Value,
                    Quality = MapQuality(dv.StatusCode),
                    SourceTimestamp = dv.SourceTimestamp != DateTime.MinValue ? dv.SourceTimestamp : (DateTime?)null,
                    ServerTimestamp = dv.ServerTimestamp != DateTime.MinValue ? dv.ServerTimestamp : (DateTime?)null
                });
            }
            return snapshots;
        }

        public Task WriteNodeAsync(string nodeId, object value, string dataType = null, CancellationToken ct = default)
        {
            // Safety gate: writing to a running PLC is high risk; reject any write unless write-enable is on.
            if (!WriteEnabled)
            {
                _logger.LogWarning("Blocked OPC UA write to {NodeId}: write is disabled (WriteEnabled=false)", nodeId);
                throw new InvalidOperationException(
                    "OPC UA writes are disabled. Writing to a running PLC is a high-risk operation; enable \"Allow writes\" in the OPC UA panel before retrying.");
            }

            EnsureConnected();

            // Audit: record every write attempt (node + target value) for later tracing.
            _logger.LogWarning("OPC UA WRITE attempt: node={NodeId}, value={Value}, dataType={DataType}", nodeId, value, dataType ?? "(auto)");

            var id = new NodeId(nodeId);
            
            // If dataType is not provided, read it from the node (Requirement 2.2)
            if (string.IsNullOrEmpty(dataType))
            {
                try
                {
                    // Read the node to get its DataType attribute
                    var node = _session.ReadNode(id);
                    if (node is VariableNode varNode && varNode.DataType != null)
                    {
                        // Get the BuiltInType from the node's data type
                        var builtInType = TypeInfo.GetBuiltInType(varNode.DataType, _session.TypeTree);
                        dataType = builtInType.ToString();
                        
                        _logger.LogDebug("Auto-detected data type '{DataType}' for node {NodeId}", dataType, nodeId);
                    }
                    
                    if (string.IsNullOrEmpty(dataType))
                    {
                        throw new InvalidOperationException($"Cannot determine data type for node {nodeId}. The node may not be a variable node or lacks a data type definition.");
                    }
                }
                catch (Exception ex) when (!(ex is InvalidOperationException))
                {
                    _logger.LogError(ex, "Cannot determine data type for node {NodeId}", nodeId);
                    throw new InvalidOperationException($"Cannot determine data type for node {nodeId}. Please specify dataType parameter.", ex);
                }
            }
            
            // Convert the value to the target data type (Requirement 2.18)
            var convertedValue = ConvertValueForWrite(value, dataType);
            
            // Write the converted value using Session.Write (Requirement 2.18)
            var dv = new DataValue(new Variant(convertedValue));
            var writeValue = new WriteValue { NodeId = id, AttributeId = Attributes.Value, Value = dv };
            var writeValues = new WriteValueCollection { writeValue };

            _session.Write(null, writeValues, out StatusCodeCollection results, out DiagnosticInfoCollection _);
            
            // Check StatusCode - throw if Bad or Uncertain (Requirement 2.19, 2.20)
            var sc = results[0];
            if (StatusCode.IsBad(sc) || StatusCode.IsUncertain(sc))
            {
                _logger.LogError("Write failed for node {NodeId} with status code {StatusCode}", nodeId, sc);
                throw new InvalidOperationException($"Write failed for {nodeId}: {sc}");
            }
            
            _logger.LogDebug("Successfully wrote value to node {NodeId}", nodeId);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Converts a value to the target OPC UA data type.
        /// Handles string inputs and numeric type conversions.
        /// </summary>
        /// <param name="value">The input value to convert</param>
        /// <param name="targetDataType">The target OPC UA data type (Boolean, Int16, Int32, Int64, Float, Double, String)</param>
        /// <returns>The converted value</returns>
        /// <exception cref="ArgumentException">Thrown when conversion is not possible</exception>
        private object ConvertValueForWrite(object value, string targetDataType)
        {
            if (value == null)
                throw new ArgumentNullException(nameof(value), "Value cannot be null");
            
            if (string.IsNullOrEmpty(targetDataType))
                throw new ArgumentException("Target data type cannot be null or empty", nameof(targetDataType));
            
            // Validate targetDataType is a supported type (NFR12, NFR14)
            var supportedTypes = new[] { "Boolean", "Int16", "Int32", "Int64", "Float", "Double", "String" };
            if (!supportedTypes.Contains(targetDataType))
            {
                throw new ArgumentException(
                    $"Unsupported target data type: '{targetDataType}'. Supported types are: {string.Join(", ", supportedTypes)}", 
                    nameof(targetDataType));
            }
            
            // Check if value already matches target type
            var valueType = value.GetType();
            
            // Type matching check
            if (IsTypeMatch(valueType, targetDataType))
                return value;
            
            // String to target type conversions
            if (value is string stringValue)
            {
                return ConvertStringToType(stringValue, targetDataType);
            }
            
            // Numeric type conversions
            if (IsNumericType(valueType))
            {
                return ConvertNumericType(value, targetDataType);
            }
            
            // Any type can be converted to its string representation.
            if (targetDataType == "String")
            {
                return value.ToString();
            }
            
            // No valid conversion found
            throw new ArgumentException($"Cannot convert '{value}' of type {valueType.Name} to {targetDataType}");
        }

        private bool IsTypeMatch(Type valueType, string targetDataType)
        {
            switch (targetDataType)
            {
                case "Boolean":
                    return valueType == typeof(bool);
                case "Int16":
                    return valueType == typeof(short);
                case "Int32":
                    return valueType == typeof(int);
                case "Int64":
                    return valueType == typeof(long);
                case "Float":
                    return valueType == typeof(float);
                case "Double":
                    return valueType == typeof(double);
                case "String":
                    return valueType == typeof(string);
                default:
                    return false;
            }
        }

        private object ConvertStringToType(string stringValue, string targetDataType)
        {
            switch (targetDataType)
            {
                case "Boolean":
                    // Handle standard boolean string representations
                    if (stringValue == "true" || stringValue == "True" || stringValue == "TRUE" || stringValue == "1")
                        return true;
                    if (stringValue == "false" || stringValue == "False" || stringValue == "FALSE" || stringValue == "0")
                        return false;
                    throw new ArgumentException($"Cannot convert '{stringValue}' to Boolean. Valid values are: true, True, TRUE, 1, false, False, FALSE, 0");

                case "Int16":
                    if (short.TryParse(stringValue, out short int16Value))
                        return int16Value;
                    throw new ArgumentException($"Cannot convert '{stringValue}' to Int16. Value must be between {short.MinValue} and {short.MaxValue}");

                case "Int32":
                    if (int.TryParse(stringValue, out int int32Value))
                        return int32Value;
                    throw new ArgumentException($"Cannot convert '{stringValue}' to Int32. Value must be between {int.MinValue} and {int.MaxValue}");

                case "Int64":
                    if (long.TryParse(stringValue, out long int64Value))
                        return int64Value;
                    throw new ArgumentException($"Cannot convert '{stringValue}' to Int64. Value must be between {long.MinValue} and {long.MaxValue}");

                case "Float":
                    if (float.TryParse(stringValue, out float floatValue))
                        return floatValue;
                    throw new ArgumentException($"Cannot convert '{stringValue}' to Float. Invalid numeric format");

                case "Double":
                    if (double.TryParse(stringValue, out double doubleValue))
                        return doubleValue;
                    throw new ArgumentException($"Cannot convert '{stringValue}' to Double. Invalid numeric format");

                case "String":
                    return stringValue;

                default:
                    throw new ArgumentException($"Unsupported target data type: {targetDataType}");
            }
        }

        private bool IsNumericType(Type type)
        {
            return type == typeof(byte) || type == typeof(sbyte) ||
                   type == typeof(short) || type == typeof(ushort) ||
                   type == typeof(int) || type == typeof(uint) ||
                   type == typeof(long) || type == typeof(ulong) ||
                   type == typeof(float) || type == typeof(double) ||
                   type == typeof(decimal);
        }

        private object ConvertNumericType(object value, string targetDataType)
        {
            try
            {
                switch (targetDataType)
                {
                    case "Boolean":
                        // Numeric to boolean: 0 = false, non-zero = true
                        return Convert.ToBoolean(value);
                    case "Int16":
                        return Convert.ToInt16(value);
                    case "Int32":
                        return Convert.ToInt32(value);
                    case "Int64":
                        return Convert.ToInt64(value);
                    case "Float":
                        return Convert.ToSingle(value);
                    case "Double":
                        return Convert.ToDouble(value);
                    case "String":
                        return value.ToString();
                    default:
                        throw new ArgumentException($"Unsupported target data type: {targetDataType}");
                }
            }
            catch (OverflowException)
            {
                throw new ArgumentException($"Cannot convert '{value}' to {targetDataType}. Value is outside the valid range");
            }
            catch (InvalidCastException)
            {
                throw new ArgumentException($"Cannot convert '{value}' to {targetDataType}. Invalid type conversion");
            }
        }

        public Task<string> SubscribeAsync(IEnumerable<string> nodeIds, int publishingIntervalMs, CancellationToken ct = default)
        {
            // Validate parameters first (NFR12, NFR14) — argument checks precede connection state.
            if (nodeIds == null)
                throw new ArgumentNullException(nameof(nodeIds), "Node IDs cannot be null");
            
            var nodeIdsList = nodeIds.ToList();
            
            if (nodeIdsList.Count == 0)
                throw new ArgumentException("Node IDs cannot be empty. At least one node ID must be provided", nameof(nodeIds));
            
            if (publishingIntervalMs <= 0)
                throw new ArgumentException($"Publishing interval must be greater than 0, but was {publishingIntervalMs}", nameof(publishingIntervalMs));

            EnsureConnected();
            
            _logger.LogInformation("Creating OPC UA subscription for {NodeCount} nodes with publishing interval {IntervalMs}ms", 
                nodeIdsList.Count, publishingIntervalMs);
            
            // Step 1: Generate unique subscription ID (GUID format)
            var subscriptionId = Guid.NewGuid().ToString();
            
            // Step 2: Create OPC UA Subscription object with proper settings
            var subscription = new Subscription(_session.DefaultSubscription)
            {
                PublishingInterval = publishingIntervalMs,
                PublishingEnabled = true,
                LifetimeCount = 10000,
                KeepAliveCount = 100,
                MaxNotificationsPerPublish = 1000
            };

            // Step 3: Create MonitoredItem for each NodeId
            var monitoredItems = nodeIdsList.Select(nodeIdStr =>
            {
                var monitoredItem = new MonitoredItem(subscription.DefaultItem)
                {
                    DisplayName = nodeIdStr,
                    StartNodeId = new NodeId(nodeIdStr),
                    AttributeId = Attributes.Value,
                    SamplingInterval = publishingIntervalMs,
                    QueueSize = 10,
                    DiscardOldest = true
                };
                
                // Step 4: Attach notification handler to each MonitoredItem
                monitoredItem.Notification += OnMonitoredItemNotification;
                
                return monitoredItem;
            }).ToList();

            // Step 5: Add all monitored items to subscription (batch operation)
            subscription.AddItems(monitoredItems);
            
            // Step 6: Add subscription to session
            _session.AddSubscription(subscription);
            
            // Step 7: Activate the subscription
            subscription.Create();
            
            // Step 8: Create SubscriptionInfo and store in dictionary
            var subscriptionInfo = new SubscriptionInfo
            {
                SubscriptionId = subscriptionId,
                OpcSubscription = subscription,
                NodeIds = nodeIdsList,
                PublishingIntervalMs = publishingIntervalMs,
                CreatedAt = DateTime.UtcNow
            };
            
            _subscriptions[subscriptionId] = subscriptionInfo;
            
            _logger.LogInformation("Successfully created OPC UA subscription {SubscriptionId} for {NodeCount} nodes", 
                subscriptionId, nodeIdsList.Count);
            
            // Step 9: Return unique subscriptionId
            return Task.FromResult(subscriptionId);
        }

        public Task UnsubscribeAsync(string subscriptionId, CancellationToken ct = default)
        {
            _logger.LogInformation("Unsubscribing from OPC UA subscription {SubscriptionId}", subscriptionId);
            
            if (_subscriptions.TryGetValue(subscriptionId, out var subscriptionInfo))
            {
                // Remove from dictionary first
                _subscriptions.Remove(subscriptionId);
                
                // Detach notification handlers
                foreach (var item in subscriptionInfo.OpcSubscription.MonitoredItems)
                {
                    item.Notification -= OnMonitoredItemNotification;
                }
                
                // Delete subscription
                subscriptionInfo.OpcSubscription.Delete(true);
                _session?.RemoveSubscription(subscriptionInfo.OpcSubscription);
                
                _logger.LogInformation("Successfully unsubscribed from subscription {SubscriptionId}", subscriptionId);
            }
            else
            {
                _logger.LogWarning("Subscription {SubscriptionId} not found for unsubscribe", subscriptionId);
            }
            
            return Task.CompletedTask;
        }

        /// <summary>
        /// Handles data change notifications from OPC UA monitored items.
        /// Extracts nodeId, value, dataType, and sourceTimestamp information,
        /// then triggers the DataChanged event with subscriptionId and snapshot.
        /// </summary>
        private void OnMonitoredItemNotification(MonitoredItem item, MonitoredItemNotificationEventArgs e)
        {
            if (e.NotificationValue is MonitoredItemNotification notification)
            {
                // Extract information from the notification
                var nodeId = item.StartNodeId.ToString();
                var value = notification.Value.Value;
                var dataType = notification.Value.WrappedValue.TypeInfo?.BuiltInType.ToString();
                var sourceTimestamp = notification.Value.SourceTimestamp != DateTime.MinValue 
                    ? notification.Value.SourceTimestamp 
                    : (DateTime?)null;
                
                // Create value snapshot with extracted information
                var snapshot = new OpcUaValueSnapshot
                {
                    NodeId = nodeId,
                    DisplayName = item.DisplayName,
                    Value = value,
                    DataType = dataType,
                    Quality = MapQuality(notification.Value.StatusCode),
                    SourceTimestamp = sourceTimestamp,
                    ServerTimestamp = notification.Value.ServerTimestamp != DateTime.MinValue 
                        ? notification.Value.ServerTimestamp 
                        : (DateTime?)null
                };
                
                // Find the subscriptionId for this subscription
                var subscriptionId = FindSubscriptionId(item.Subscription);
                
                // Trigger DataChanged event with subscriptionId and snapshot
                if (subscriptionId != null)
                {
                    DataChanged?.Invoke(subscriptionId, snapshot);
                }
            }
        }
        
        /// <summary>
        /// Finds the subscriptionId for a given OPC UA Subscription object.
        /// </summary>
        /// <param name="subscription">The OPC UA subscription to find</param>
        /// <returns>The subscriptionId or null if not found</returns>
        private string FindSubscriptionId(Subscription subscription)
        {
            if (subscription == null)
                return null;
            
            foreach (var kvp in _subscriptions)
            {
                if (kvp.Value.OpcSubscription == subscription)
                {
                    return kvp.Key;
                }
            }
            
            return null;
        }

        public Task<IReadOnlyList<OpcUaNodeInfo>> BrowseAsync(string nodeId = null, CancellationToken ct = default)
        {
            EnsureConnected();
            
            // Step 1: Initialize root node - use ObjectsFolder if nodeId is null
            var startNodeId = string.IsNullOrEmpty(nodeId) ? ObjectIds.ObjectsFolder : new NodeId(nodeId);
            
            // Step 2: Configure browse parameters
            var browseDesc = new BrowseDescription
            {
                NodeId = startNodeId,
                BrowseDirection = BrowseDirection.Forward,
                ReferenceTypeId = ReferenceTypeIds.HierarchicalReferences,
                IncludeSubtypes = true,
                NodeClassMask = (uint)(NodeClass.Object | NodeClass.Variable),
                ResultMask = (uint)BrowseResultMask.All
            };
            var browseDescs = new BrowseDescriptionCollection { browseDesc };

            // Step 3: browse, handling pagination via ContinuationPoint.
            // Key fix: directories like Memory / DB can hold many variables, so Browse
            // returns only one page and remaining references must be fetched with BrowseNext.
            // Previously the continuation point was ignored, so M variables etc. were
            // dropped and the tree looked "empty" after expanding.
            var allRefs = new ReferenceDescriptionCollection();

            _session.Browse(null, null, 0, browseDescs,
                out BrowseResultCollection results, out DiagnosticInfoCollection _);

            var first = results != null && results.Count > 0 ? results[0] : null;
            if (first?.References != null) allRefs.AddRange(first.References);

            var continuationPoint = first?.ContinuationPoint;
            while (continuationPoint != null && continuationPoint.Length > 0)
            {
                _session.BrowseNext(null, false, new ByteStringCollection { continuationPoint },
                    out BrowseResultCollection nextResults, out DiagnosticInfoCollection _);

                var nr = nextResults != null && nextResults.Count > 0 ? nextResults[0] : null;
                if (nr?.References != null) allRefs.AddRange(nr.References);
                continuationPoint = nr?.ContinuationPoint;
            }

            if (allRefs.Count == 0)
            {
                return Task.FromResult<IReadOnlyList<OpcUaNodeInfo>>(Array.Empty<OpcUaNodeInfo>());
            }

            // Step 4: Create OpcUaNodeInfo objects for each browse result
            var nodeInfos = new List<OpcUaNodeInfo>();
            foreach (var reference in allRefs)
            {
                var displayName = reference.DisplayName?.Text ?? reference.BrowseName?.Name ?? reference.NodeId.ToString();

                // Filter out noise property nodes: every Siemens folder/object carries an
                // "Icon" property (i=2003) that is not a meaningful variable; showing a
                // pile of "Icon" entries made nodes look empty.
                if (string.Equals(displayName, "Icon", StringComparison.OrdinalIgnoreCase))
                    continue;

                var nodeInfo = new OpcUaNodeInfo
                {
                    NodeId = reference.NodeId.ToString(),
                    DisplayName = displayName,
                    NodeClass = reference.NodeClass.ToString(),
                    ParentNodeId = startNodeId.ToString(),
                    Level = 0  // Root level nodes have Level = 0
                };
                
                // Set DataType for Variable nodes if available
                if (reference.NodeClass == NodeClass.Variable)
                {
                    try
                    {
                        var refNodeId = ExpandedNodeId.ToNodeId(reference.NodeId, _session.NamespaceUris);
                        var dataTypeNode = _session.ReadNode(refNodeId);
                        if (dataTypeNode is VariableNode varNode && varNode.DataType != null)
                        {
                            // Prefer a readable built-in type name (e.g. Boolean/Int16/Real) over a raw NodeId.
                            try
                            {
                                var builtIn = TypeInfo.GetBuiltInType(varNode.DataType, _session.TypeTree);
                                nodeInfo.DataType = builtIn != BuiltInType.Null ? builtIn.ToString() : varNode.DataType.ToString();
                            }
                            catch
                            {
                                nodeInfo.DataType = varNode.DataType.ToString();
                            }
                        }
                    }
                    catch
                    {
                        nodeInfo.DataType = null;
                    }
                }
                
                nodeInfos.Add(nodeInfo);
            }

            return Task.FromResult<IReadOnlyList<OpcUaNodeInfo>>(nodeInfos);
        }

        /// <summary>
        /// Recursively browses the OPC UA node hierarchy, building a full tree.
        /// </summary>
        /// <param name="nodeId">Starting node id; ObjectsFolder when null</param>
        /// <param name="maxDepth">Maximum recursion depth (default 3) to avoid performance issues from deep recursion</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>Hierarchical list of nodes including their children</returns>
        public Task<IReadOnlyList<OpcUaNodeInfo>> BrowseRecursiveAsync(string nodeId = null, int maxDepth = 3, CancellationToken ct = default)
        {
            EnsureConnected();
            
            _logger.LogInformation("Starting recursive browse from node {NodeId} with max depth {MaxDepth}", nodeId ?? "ObjectsFolder", maxDepth);
            
            // Step 1: Initialize root node - use ObjectsFolder if nodeId is null
            var startNodeId = string.IsNullOrEmpty(nodeId) ? ObjectIds.ObjectsFolder : new NodeId(nodeId);
            
            // Step 2: Initialize visitedNodes set to prevent circular references
            var visitedNodes = new HashSet<NodeId>();
            visitedNodes.Add(startNodeId);
            
            // Step 3: Browse root level
            var browseDesc = new BrowseDescription
            {
                NodeId = startNodeId,
                BrowseDirection = BrowseDirection.Forward,
                ReferenceTypeId = ReferenceTypeIds.HierarchicalReferences,
                IncludeSubtypes = true,
                NodeClassMask = (uint)(NodeClass.Object | NodeClass.Variable),
                ResultMask = (uint)BrowseResultMask.All
            };
            var browseDescs = new BrowseDescriptionCollection { browseDesc };

            _session.Browse(null, null, 0, browseDescs,
                out BrowseResultCollection results, out DiagnosticInfoCollection _);
            
            var refs = results[0]?.References;
            if (refs == null || refs.Count == 0)
            {
                _logger.LogInformation("No child nodes found for node {NodeId}", startNodeId);
                return Task.FromResult<IReadOnlyList<OpcUaNodeInfo>>(Array.Empty<OpcUaNodeInfo>());
            }

            // Step 4: Create OpcUaNodeInfo objects for root level and recursively browse children
            var rootNodes = new List<OpcUaNodeInfo>();
            foreach (var reference in refs)
            {
                // Convert ExpandedNodeId to NodeId for comparison
                var refNodeId = ExpandedNodeId.ToNodeId(reference.NodeId, _session.NamespaceUris);
                
                // Skip if already visited (circular reference protection)
                if (visitedNodes.Contains(refNodeId))
                {
                    _logger.LogDebug("Skipping already visited node {NodeId}", refNodeId);
                    continue;
                }
                
                var nodeInfo = new OpcUaNodeInfo
                {
                    NodeId = refNodeId.ToString(),
                    DisplayName = reference.DisplayName?.Text ?? refNodeId.ToString(),
                    NodeClass = reference.NodeClass.ToString(),
                    ParentNodeId = startNodeId.ToString(),
                    Level = 0  // Root level nodes have Level = 0
                };
                
                // Set DataType for Variable nodes if available
                if (reference.NodeClass == NodeClass.Variable && reference.TypeDefinition != null)
                {
                    try
                    {
                        var dataTypeNode = _session.ReadNode(refNodeId);
                        if (dataTypeNode is VariableNode varNode)
                        {
                            nodeInfo.DataType = varNode.DataType?.ToString();
                        }
                    }
                    catch
                    {
                        nodeInfo.DataType = null;
                    }
                }
                
                // Mark as visited
                visitedNodes.Add(refNodeId);
                
                // Step 5: Recursively browse children if maxDepth > 1
                if (maxDepth > 1)
                {
                    nodeInfo.Children = BrowseChildrenRecursive(refNodeId, 1, maxDepth, visitedNodes);
                }
                
                rootNodes.Add(nodeInfo);
            }
            
            _logger.LogInformation("Recursive browse completed, found {NodeCount} root nodes", rootNodes.Count);
            return Task.FromResult<IReadOnlyList<OpcUaNodeInfo>>(rootNodes);
        }

        /// <summary>
        /// Private helper that recursively browses child nodes.
        /// </summary>
        /// <param name="parentNodeId">Parent node id</param>
        /// <param name="currentDepth">Current depth</param>
        /// <param name="maxDepth">Maximum depth</param>
        /// <param name="visitedNodes">Set of visited nodes, used to prevent circular references</param>
        /// <returns>List of child nodes</returns>
        private List<OpcUaNodeInfo> BrowseChildrenRecursive(NodeId parentNodeId, int currentDepth, int maxDepth, HashSet<NodeId> visitedNodes)
        {
            var children = new List<OpcUaNodeInfo>();
            
            // Stop recursion at max depth
            if (currentDepth >= maxDepth)
            {
                return children;
            }
            
            try
            {
                // Configure browse parameters
                var browseDesc = new BrowseDescription
                {
                    NodeId = parentNodeId,
                    BrowseDirection = BrowseDirection.Forward,
                    ReferenceTypeId = ReferenceTypeIds.HierarchicalReferences,
                    IncludeSubtypes = true,
                    NodeClassMask = (uint)(NodeClass.Object | NodeClass.Variable),
                    ResultMask = (uint)BrowseResultMask.All
                };
                var browseDescs = new BrowseDescriptionCollection { browseDesc };

                // Execute browse operation
                _session.Browse(null, null, 0, browseDescs,
                    out BrowseResultCollection results, out DiagnosticInfoCollection _);
                
                var refs = results[0]?.References;
                if (refs == null || refs.Count == 0)
                {
                    return children;
                }

                // Process each child reference
                foreach (var reference in refs)
                {
                    // Convert ExpandedNodeId to NodeId for comparison
                    var refNodeId = ExpandedNodeId.ToNodeId(reference.NodeId, _session.NamespaceUris);
                    
                    // Skip if already visited (circular reference protection)
                    if (visitedNodes.Contains(refNodeId))
                    {
                        _logger.LogDebug("Skipping already visited node {NodeId} at depth {Depth}", refNodeId, currentDepth);
                        continue;
                    }
                    
                    var childInfo = new OpcUaNodeInfo
                    {
                        NodeId = refNodeId.ToString(),
                        DisplayName = reference.DisplayName?.Text ?? refNodeId.ToString(),
                        NodeClass = reference.NodeClass.ToString(),
                        ParentNodeId = parentNodeId.ToString(),
                        Level = currentDepth  // Set correct level depth
                    };
                    
                    // Set DataType for Variable nodes if available
                    if (reference.NodeClass == NodeClass.Variable && reference.TypeDefinition != null)
                    {
                        try
                        {
                            var dataTypeNode = _session.ReadNode(refNodeId);
                            if (dataTypeNode is VariableNode varNode)
                            {
                                childInfo.DataType = varNode.DataType?.ToString();
                            }
                        }
                        catch
                        {
                            childInfo.DataType = null;
                        }
                    }
                    
                    // Mark as visited
                    visitedNodes.Add(refNodeId);
                    
                    // Recurse to next level if not at max depth
                    if (currentDepth + 1 < maxDepth)
                    {
                        childInfo.Children = BrowseChildrenRecursive(refNodeId, currentDepth + 1, maxDepth, visitedNodes);
                    }
                    
                    children.Add(childInfo);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error browsing children of node {NodeId} at depth {Depth}", parentNodeId, currentDepth);
                // Return partial results instead of throwing
            }
            
            return children;
        }

        private static OpcUaQuality MapQuality(StatusCode sc)
        {
            if (StatusCode.IsGood(sc)) return OpcUaQuality.Good;
            if (StatusCode.IsUncertain(sc)) return OpcUaQuality.Uncertain;
            return OpcUaQuality.Bad;
        }

        public void Dispose()
        {
            _reconnectTimer?.Dispose();
            _session?.Dispose();
        }
    }
}
