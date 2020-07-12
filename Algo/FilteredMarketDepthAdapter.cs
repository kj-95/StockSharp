namespace StockSharp.Algo
{
	using System;
	using System.Collections.Generic;
	using System.Linq;

	using Ecng.Collections;
	using Ecng.Common;

	using StockSharp.Messages;
	using StockSharp.Logging;
	using StockSharp.Localization;

	/// <summary>
	/// Filtered market depth adapter.
	/// </summary>
	public class FilteredMarketDepthAdapter : MessageAdapterWrapper
	{
		private class FilteredMarketDepthInfo
		{
			private readonly Dictionary<ValueTuple<Sides, decimal>, decimal> _totals = new Dictionary<ValueTuple<Sides, decimal>, decimal>();
			private readonly Dictionary<long, RefTriple<Sides, decimal, decimal?>> _activeOrders = new Dictionary<long, RefTriple<Sides, decimal, decimal?>>();
			
			private QuoteChangeMessage _lastSnapshot;

			public FilteredMarketDepthInfo(long subscribeId, Subscription bookSubscription, Subscription ordersSubscription)
			{
				SubscribeId = subscribeId;

				BookSubscription = bookSubscription ?? throw new ArgumentNullException(nameof(bookSubscription));
				OrdersSubscription = ordersSubscription ?? throw new ArgumentNullException(nameof(ordersSubscription));
			}

			public long SubscribeId { get; }
			public long UnSubscribeId { get; set; }

			public Subscription BookSubscription { get; }
			public Subscription OrdersSubscription { get; }

			public OnlineInfo Online { get; set; }

			//public SubscriptionStates State { get; set; } = SubscriptionStates.Stopped;

			private QuoteChange[] Filter(Sides side, IEnumerable<QuoteChange> quotes)
			{
				return quotes
					.Select(quote =>
					{
						if (_totals.TryGetValue((side, quote.Price), out var total))
							quote.Volume -= total;

						return quote;
					})
					.Where(q => q.Volume > 0)
					.ToArray();
			}

			private QuoteChangeMessage CreateFilteredBook()
			{
				var book = new QuoteChangeMessage
				{
					SecurityId = _lastSnapshot.SecurityId,
					ServerTime = _lastSnapshot.ServerTime,
					LocalTime = _lastSnapshot.LocalTime,
					IsSorted = _lastSnapshot.IsSorted,
					BuildFrom = _lastSnapshot.BuildFrom,
					Currency = _lastSnapshot.Currency,
					IsFiltered = true,
					Bids = Filter(Sides.Buy, _lastSnapshot.Bids),
					Asks = Filter(Sides.Sell, _lastSnapshot.Asks),
				};

				if (Online == null)
					book.SetSubscriptionIds(subscriptionId: SubscribeId);
				else
					book.SetSubscriptionIds(Online.Subscribers.Cache);

				return book;
			}

			public QuoteChangeMessage Process(QuoteChangeMessage message)
			{
				if (message is null)
					throw new ArgumentNullException(nameof(message));

				_lastSnapshot = message.TypedClone();

				return CreateFilteredBook();
			}

			public QuoteChangeMessage Process(ExecutionMessage message)
			{
				if (message is null)
					throw new ArgumentNullException(nameof(message));

				if (message.TransactionId != 0)
				{
					if (message.OrderState != OrderStates.Done && message.OrderState != OrderStates.Failed)
					{
						var balance = message.Balance;

						_activeOrders[message.TransactionId] = RefTuple.Create(message.Side, message.OrderPrice, balance);

						if (balance == null)
							return null;

						var valKey = (message.Side, message.OrderPrice);

						if (_totals.TryGetValue(valKey, out var total))
						{
							total += balance.Value;
							_totals[valKey] = total;
						}
						else
							_totals.Add(valKey, balance.Value);
					}
				}
				else if (_activeOrders.TryGetValue(message.OriginalTransactionId, out var key))
				{
					var valKey = (key.First, key.Second);

					switch (message.OrderState)
					{
						case OrderStates.Done:
						case OrderStates.Failed:
						{
							_activeOrders.Remove(message.OriginalTransactionId);

							var balance = key.Third;

							if (balance == null)
								return null;

							var total = _totals[valKey];
							total -= balance.Value;

							if (total > 0)
								_totals[valKey] = total;
							else
								_totals.Remove(valKey);

							break;
						}

						case OrderStates.Active:
						{
							var newBalance = message.Balance;

							if (newBalance == null)
								return null;

							var prevBalance = key.Third;
							key.Third = newBalance;

							if (prevBalance == null)
							{
								if (_totals.TryGetValue(valKey, out var total))
								{
									total += newBalance.Value;
									_totals[valKey] = total;
								}
								else
									_totals.Add(valKey, newBalance.Value);
							}
							else
							{
								var delta = prevBalance.Value - newBalance.Value;

								if (delta == 0)
									return null;

								var total = _totals[valKey];
								total -= delta;

								if (total > 0)
									_totals[valKey] = total;
								else
									_totals.Remove(valKey);
							}

							break;
						}
					}
				}
				else
					return null;

				return _lastSnapshot is null ? null : CreateFilteredBook();
			}
		}

		private class OnlineInfo
		{
			public readonly CachedSynchronizedSet<long> Subscribers = new CachedSynchronizedSet<long>();
			
			public readonly CachedSynchronizedSet<long> BookSubscribers = new CachedSynchronizedSet<long>();
			public readonly CachedSynchronizedSet<long> OrdersSubscribers = new CachedSynchronizedSet<long>();
		}

		private readonly SyncObject _sync = new SyncObject();

		private readonly Dictionary<long, FilteredMarketDepthInfo> _byId = new Dictionary<long, FilteredMarketDepthInfo>();
		private readonly Dictionary<long, FilteredMarketDepthInfo> _byBookId = new Dictionary<long, FilteredMarketDepthInfo>();
		private readonly Dictionary<long, FilteredMarketDepthInfo> _byOrderStatusId = new Dictionary<long, FilteredMarketDepthInfo>();
		private readonly Dictionary<SecurityId, OnlineInfo> _online = new Dictionary<SecurityId, OnlineInfo>();
		private readonly Dictionary<long, Tuple<FilteredMarketDepthInfo, bool>> _unsubscribeRequests = new Dictionary<long, Tuple<FilteredMarketDepthInfo, bool>>();

		/// <summary>
		/// Initializes a new instance of the <see cref="AssociatedSecurityAdapter"/>.
		/// </summary>
		/// <param name="innerAdapter">Inner message adapter.</param>
		public FilteredMarketDepthAdapter(IMessageAdapter innerAdapter)
			: base(innerAdapter)
		{
		}

		/// <summary>
		/// Data type.
		/// </summary>
		public static readonly DataType Filtered = DataType.Create(typeof(QuoteChangeMessage), ExecutionTypes.Transaction).Immutable();

		/// <inheritdoc />
		protected override bool OnSendInMessage(Message message)
		{
			switch (message.Type)
			{
				case MessageTypes.Reset:
				{
					lock (_sync)
					{
						_byId.Clear();
						_byBookId.Clear();
						_byOrderStatusId.Clear();
						_online.Clear();
						_unsubscribeRequests.Clear();
					}
					
					break;
				}

				case MessageTypes.MarketData:
				{
					var mdMsg = (MarketDataMessage)message;

					if (mdMsg.IsSubscribe)
					{
						if (mdMsg.SecurityId == default)
							break;

						if (mdMsg.DataType2 != Filtered)
							break;

						var transId = mdMsg.TransactionId;

						mdMsg = mdMsg.TypedClone();
						mdMsg.TransactionId = TransactionIdGenerator.GetNextId();
						mdMsg.DataType2 = DataType.MarketDepth;

						var orderStatus = new OrderStatusMessage
						{
							TransactionId = TransactionIdGenerator.GetNextId(),
							IsSubscribe = true,
							States = new[] { OrderStates.Active },
							SecurityId = mdMsg.SecurityId,
						};

						var info = new FilteredMarketDepthInfo(transId, new Subscription(mdMsg, mdMsg), new Subscription(orderStatus, orderStatus));

						lock (_sync)
						{
							_byId.Add(transId, info);
							_byBookId.Add(mdMsg.TransactionId, info);
							_byOrderStatusId.Add(orderStatus.TransactionId, info);
						}

						base.OnSendInMessage(mdMsg);
						base.OnSendInMessage(orderStatus);

						return true;
					}
					else
					{
						MarketDataMessage bookUnsubscribe = null;
						OrderStatusMessage ordersUnsubscribe = null;

						lock (_sync)
						{
							if (!_byId.TryGetValue(mdMsg.OriginalTransactionId, out var info))
								break;

							info.UnSubscribeId = mdMsg.TransactionId;

							if (info.BookSubscription.State.IsActive())
							{
								bookUnsubscribe = new MarketDataMessage
								{
									TransactionId = TransactionIdGenerator.GetNextId(),
									OriginalTransactionId = info.BookSubscription.TransactionId,
									IsSubscribe = false,
								};

								_unsubscribeRequests.Add(bookUnsubscribe.TransactionId, Tuple.Create(info, true));
							}

							if (info.OrdersSubscription.State.IsActive())
							{
								ordersUnsubscribe = new OrderStatusMessage
								{
									TransactionId = TransactionIdGenerator.GetNextId(),
									OriginalTransactionId = info.OrdersSubscription.TransactionId,
									IsSubscribe = false,
								};

								_unsubscribeRequests.Add(ordersUnsubscribe.TransactionId, Tuple.Create(info, false));
							}
						}

						if (bookUnsubscribe == null && ordersUnsubscribe == null)
						{
							RaiseNewOutMessage(new SubscriptionResponseMessage
							{
								OriginalTransactionId = mdMsg.TransactionId,
								Error = new InvalidOperationException(LocalizedStrings.SubscriptionNonExist.Put(mdMsg.OriginalTransactionId)),
							});
						}
						else
						{
							if (bookUnsubscribe != null)
								base.OnSendInMessage(bookUnsubscribe);

							if (ordersUnsubscribe != null)
								base.OnSendInMessage(ordersUnsubscribe);
						}
							
						return true;
					}
				}
			}

			return base.OnSendInMessage(message);
		}

		/// <inheritdoc />
		protected override void OnInnerAdapterNewOutMessage(Message message)
		{
			Message TryApplyState(IOriginalTransactionIdMessage msg, SubscriptionStates state)
			{
				void TryCheckOnline(FilteredMarketDepthInfo info)
				{
					if (state != SubscriptionStates.Online)
						return;

					var book = info.BookSubscription;
					var orders = info.OrdersSubscription;

					if (info.BookSubscription.State == SubscriptionStates.Online && orders.State == SubscriptionStates.Online)
					{
						var online = _online.SafeAdd(book.SecurityId.Value);

						online.Subscribers.Add(info.SubscribeId);
						online.BookSubscribers.Add(book.TransactionId);
						online.OrdersSubscribers.Add(orders.TransactionId);

						info.Online = online;
					}
				}

				var id = msg.OriginalTransactionId;

				lock (_sync)
				{
					if (_byBookId.TryGetValue(id, out var info))
					{
						var book = info.BookSubscription;

						book.State = book.State.ChangeSubscriptionState(state, id, this);

						var subscribeId = info.SubscribeId;

						if (!state.IsActive())
						{
							if (info.Online != null)
							{
								info.Online.BookSubscribers.Remove(id);
								info.Online.OrdersSubscribers.Remove(info.OrdersSubscription.TransactionId);

								info.Online.Subscribers.Remove(subscribeId);
								info.Online = null;
							}
						}
						else
							TryCheckOnline(info);

						switch (book.State)
						{
							case SubscriptionStates.Stopped:
							case SubscriptionStates.Active:
							case SubscriptionStates.Error:
								return new SubscriptionResponseMessage { OriginalTransactionId = subscribeId, Error = (msg as IErrorMessage)?.Error };
							case SubscriptionStates.Finished:
								return new SubscriptionFinishedMessage { OriginalTransactionId = subscribeId };
							case SubscriptionStates.Online:
								return new SubscriptionOnlineMessage { OriginalTransactionId = subscribeId };
							default:
								throw new ArgumentOutOfRangeException(book.State.ToString());
						}
					}
					else if (_byOrderStatusId.TryGetValue(id, out info))
					{
						info.OrdersSubscription.State = info.OrdersSubscription.State.ChangeSubscriptionState(state, id, this);

						if (!state.IsActive())
							info.Online?.OrdersSubscribers.Remove(id);
						else
							TryCheckOnline(info);

						return null;
					}
					else if (_unsubscribeRequests.TryGetAndRemove(id, out var tuple))
					{
						info = tuple.Item1;

						if (tuple.Item2)
						{
							var book = info.BookSubscription;
							book.State = book.State.ChangeSubscriptionState(SubscriptionStates.Stopped, book.TransactionId, this);

							return new SubscriptionResponseMessage
							{
								OriginalTransactionId = info.UnSubscribeId,
								Error = (msg as IErrorMessage)?.Error,
							};
						}
						else
						{
							var orders = info.OrdersSubscription;
							orders.State = orders.State.ChangeSubscriptionState(SubscriptionStates.Stopped, orders.TransactionId, this);
							return null;
						}
					}
					else
						return (Message)msg;
				}
			}

			List<QuoteChangeMessage> filtered = null;

			switch (message.Type)
			{
				case MessageTypes.SubscriptionResponse:
				{
					var responseMsg = (SubscriptionResponseMessage)message;
					message = TryApplyState(responseMsg, responseMsg.Error == null ? SubscriptionStates.Active : SubscriptionStates.Error);
					break;
				}

				case MessageTypes.SubscriptionFinished:
				{
					message = TryApplyState((SubscriptionFinishedMessage)message, SubscriptionStates.Finished);
					break;
				}

				case MessageTypes.SubscriptionOnline:
				{
					message = TryApplyState((SubscriptionOnlineMessage)message, SubscriptionStates.Online);
					break;
				}

				case MessageTypes.QuoteChange:
				{
					var quoteMsg = (QuoteChangeMessage)message;

					if (quoteMsg.State != null)
						break;

					HashSet<long> leftIds = null;

					lock (_sync)
					{
						if (_byBookId.Count == 0)
							break;

						var ids = quoteMsg.GetSubscriptionIds();
						HashSet<long> processed = null;

						foreach (var id in ids)
						{
							if (processed != null && processed.Contains(id))
								continue;

							if (!_byBookId.TryGetValue(id, out var info))
								continue;

							var book = info.Process(quoteMsg);

							if (leftIds is null)
								leftIds = new HashSet<long>(ids);

							if (info.Online is null)
								leftIds.Remove(id);
							else
							{
								if (processed is null)
									processed = new HashSet<long>();

								processed.AddRange(info.Online.BookSubscribers.Cache);
								leftIds.RemoveRange(info.Online.BookSubscribers.Cache);
							}

							if (filtered == null)
								filtered = new List<QuoteChangeMessage>();

							filtered.Add(book);
						}
					}

					if (leftIds is null)
						break;
					else if (leftIds.Count == 0)
						message = null;
					else
						quoteMsg.SetSubscriptionIds(leftIds.ToArray());

					break;
				}

				case MessageTypes.Execution:
				{
					var execMsg = (ExecutionMessage)message;

					if	(
							execMsg.ExecutionType != ExecutionTypes.Transaction ||
							!execMsg.HasOrderInfo() ||
							execMsg.OrderPrice == 0 // ignore market orders
						)
						break;

					HashSet<long> leftIds = null;

					lock (_sync)
					{
						if (_byOrderStatusId.Count == 0)
							break;

						var ids = execMsg.GetSubscriptionIds();
						HashSet<long> processed = null;

						foreach (var id in ids)
						{
							if (processed != null && processed.Contains(id))
								continue;

							if (!_byOrderStatusId.TryGetValue(id, out var info))
								continue;

							if (leftIds is null)
								leftIds = new HashSet<long>(ids);

							if (info.Online is null)
								leftIds.Remove(id);
							else
							{
								if (processed is null)
									processed = new HashSet<long>();

								processed.AddRange(info.Online.OrdersSubscribers.Cache);
								leftIds.RemoveRange(info.Online.OrdersSubscribers.Cache);
							}

							if (filtered is null)
								filtered = new List<QuoteChangeMessage>();

							var book = info.Process(execMsg);

							if (book is object)
								filtered.Add(book);
						}
					}

					if (leftIds is null)
						break;
					else if (leftIds.Count == 0)
						message = null;
					else
						execMsg.SetSubscriptionIds(leftIds.ToArray());

					break;
				}
			}

			if (message != null)
				base.OnInnerAdapterNewOutMessage(message);

			if (filtered != null)
			{
				foreach (var book in filtered)
					base.OnInnerAdapterNewOutMessage(book);
			}
		}

		/// <summary>
		/// Create a copy of <see cref="FilteredMarketDepthAdapter"/>.
		/// </summary>
		/// <returns>Copy.</returns>
		public override IMessageChannel Clone() => new FilteredMarketDepthAdapter(InnerAdapter);
	}
}