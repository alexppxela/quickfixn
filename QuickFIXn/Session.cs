using System;
using System.Collections.Generic;
using System.Threading;
using QuickFix.Fields;
using QuickFix.Fields.Converters;

namespace QuickFix
{
    /// <summary>
    /// The Session is the primary FIX abstraction for message communication. 
    /// It performs sequencing and error recovery and represents a communication
    /// channel to a counterparty. Sessions are independent of specific communication
    /// layer connections. A Session is defined as starting with message sequence number
    /// of 1 and ending when the session is reset. The Session could span many sequential
    /// connections (it cannot operate on multiple connections simultaneously).
    /// </summary>
    public class Session : IDisposable
    {
        #region Private Members

        private static readonly Dictionary<SessionID, Session> _sessions = new();
        private readonly object _sync = new();
        private IResponder _responder;
        private readonly SessionSchedule _schedule;
        private readonly SessionState _state;
        private readonly IMessageFactory _msgFactory;
        private readonly bool _appDoesEarlyIntercept;
        private static readonly HashSet<string> AdminMsgTypes = new() { "0", "A", "1", "2", "3", "4", "5" };

        #endregion

        #region Properties

        // state
        public IMessageStore MessageStore => _state.MessageStore;
        public ILog Log => _state.Log;
        public bool IsInitiator => _state.IsInitiator;
        public bool IsAcceptor => !_state.IsInitiator;
        public bool IsEnabled => _state.IsEnabled;
        public bool IsSessionTime => _schedule.IsSessionTime(DateTime.UtcNow);
        public bool IsLoggedOn => ReceivedLogon && SentLogon;
        public bool SentLogon => _state.SentLogon;
        public bool ReceivedLogon => _state.ReceivedLogon;

        public bool IsNewSession
        {
            get
            {
                DateTime? creationTime = _state.CreationTime;
                return creationTime.HasValue == false
                    || _schedule.IsNewSession(creationTime.Value, DateTime.UtcNow);
            }
        }


        /// <summary>
        /// Session setting for heartbeat interval (in seconds)
        /// </summary>
        public int HeartBtInt => _state.HeartBtInt;

        /// <summary>
        /// Session setting for enabling message latency checks
        /// </summary>
        public bool CheckLatency { get; set; }

        /// <summary>
        /// Session setting for maximum message latency (in seconds)
        /// </summary>
        public int MaxLatency { get; set; }

        /// <summary>
        /// Send a logout if counterparty times out and does not heartbeat
        /// in response to a TestRequeset. Defaults to false
        /// </summary>
        public bool SendLogoutBeforeTimeoutDisconnect { get; set; }

        /// <summary>
        /// Gets or sets the next expected outgoing sequence number
        /// </summary>
        public SeqNumType NextSenderMsgSeqNum
        {
            get => _state.NextSenderMsgSeqNum;
            set => _state.NextSenderMsgSeqNum = value;
        }

        /// <summary>
        /// Gets or sets the next expected incoming sequence number
        /// </summary>
        public SeqNumType NextTargetMsgSeqNum
        {
            get => _state.NextTargetMsgSeqNum;
            set => _state.NextTargetMsgSeqNum = value;
        }

        /// <summary>
        /// Logon timeout in seconds
        /// </summary>
        public int LogonTimeout
        {
            get => _state.LogonTimeout;
            set => _state.LogonTimeout = value;
        }

        /// <summary>
        /// Logout timeout in seconds
        /// </summary>
        public int LogoutTimeout
        {
            get => _state.LogoutTimeout;
            set => _state.LogoutTimeout = value;
        }

        // unsynchronized properties
        /// <summary>
        /// Whether to persist messages or not. Setting to false forces quickfix
        /// to always send GapFills instead of resending messages.
        /// </summary>
        public bool PersistMessages { get; set; }

        /// <summary>
        /// Determines if session state should be restored from persistance
        /// layer when logging on. Useful for creating hot failover sessions.
        /// </summary>
        public bool RefreshOnLogon { get; set; }

        /// <summary>
        /// Reset sequence numbers on logon request
        /// </summary>
        public bool ResetOnLogon { get; set; }

        /// <summary>
        /// Reset sequence numbers to 1 after a normal logout
        /// </summary>
        public bool ResetOnLogout { get; set; }

        /// <summary>
        /// Reset sequence numbers to 1 after abnormal termination
        /// </summary>
        public bool ResetOnDisconnect { get; set; }

        /// <summary>
        /// Whether to send redundant resend requests
        /// </summary>
        public bool SendRedundantResendRequests { get; set; }

        /// <summary>
        /// Whether to resend session level rejects (msg type '3') when servicing a resend request
        /// </summary>
        public bool ResendSessionLevelRejects { get; set; }

        /// <summary>
        /// Whether to validate length and checksum of messages
        /// </summary>
        public bool ValidateLengthAndChecksum { get; set; }

        /// <summary>
        /// Validates Comp IDs for each message
        /// </summary>
        public bool CheckCompID { get; set; }

        /// <summary>
        /// Determines if milliseconds should be added to timestamps.
        /// Only avilable on FIX4.2. or greater
        /// </summary>
        public bool MillisecondsInTimeStamp
        {
            get => TimeStampPrecision == TimeStampPrecision.Millisecond;
            set => TimeStampPrecision = value ? TimeStampPrecision.Millisecond : TimeStampPrecision.Second;
        }

        /// <summary>
        /// Gets or sets the time stamp precision.
        /// </summary>
        /// <value>
        /// The time stamp precision.
        /// </value>
        public TimeStampPrecision TimeStampPrecision
        {
            get;
            set;
        }

        /// <summary>
        /// Adds the last message sequence number processed in the header (tag 369)
        /// </summary>
        public bool EnableLastMsgSeqNumProcessed { get; set; }

        /// <summary>
        /// Ignores resend requests marked poss dup
        /// </summary>
        public bool IgnorePossDupResendRequests { get; set; }

        /// <summary>
        /// Sets a maximum number of messages to request in a resend request.
        /// </summary>
        public SeqNumType MaxMessagesInResendRequest { get; set; }

        /// <summary>
        /// This is the FIX field value, e.g. "6" for FIX44
        /// </summary>
        public ApplVerID targetDefaultApplVerID { get; set; }

        /// <summary>
        /// This is the FIX field value, e.g. "6" for FIX44
        /// </summary>
        public string SenderDefaultApplVerID { get; set; }

        public SessionID SessionID { get; set; }
        public IApplication Application { get; set; }
        public DataDictionaryProvider DataDictionaryProvider { get; set; }
        public DataDictionary.DataDictionary SessionDataDictionary { get; private set; }
        public DataDictionary.DataDictionary ApplicationDataDictionary { get; private set; }

        /// <summary>
        /// Returns whether the Session has a Responder. This method is synchronized
        /// </summary>
        public bool HasResponder { get { Thread.MemoryBarrier(); return _responder is not null; } }

        /// <summary>
        /// Returns whether the Sessions will allow ResetSequence messages sent as
        /// part of a resend request (PossDup=Y) to omit the OrigSendingTime
        /// </summary>
        public bool RequiresOrigSendingTime { get; set; }

        #endregion

        public Session(
            bool isInitiator, IApplication app, IMessageStoreFactory storeFactory, SessionID sessID, DataDictionaryProvider dataDictProvider,
            SessionSchedule sessionSchedule, int heartBtInt, ILogFactory logFactory, IMessageFactory msgFactory, string senderDefaultApplVerID)
        {
            _schedule = sessionSchedule;
            _msgFactory = msgFactory;
            _appDoesEarlyIntercept = app is IApplicationExt;

            this.Application = app;
            this.SessionID = sessID;
            this.DataDictionaryProvider = new DataDictionaryProvider(dataDictProvider);
            this.SenderDefaultApplVerID = senderDefaultApplVerID;

            this.SessionDataDictionary = this.DataDictionaryProvider.GetSessionDataDictionary(this.SessionID.BeginString);
            this.ApplicationDataDictionary = this.SessionID.IsFIXT
                ? this.DataDictionaryProvider.GetApplicationDataDictionary(this.SenderDefaultApplVerID)
                : this.SessionDataDictionary;

            ILog log = logFactory is not null ? logFactory.Create(sessID) : new NullLog();

            _state = new SessionState(isInitiator, log, heartBtInt)
            {
                MessageStore = storeFactory.Create(sessID)
            };

            // Configuration defaults.
            // Will be overridden by the SessionFactory with values in the user's configuration.
            this.PersistMessages = true;
            this.ResetOnDisconnect = false;
            this.SendRedundantResendRequests = false;
            this.ResendSessionLevelRejects = false;
            this.ValidateLengthAndChecksum = true;
            this.CheckCompID = true;
            this.TimeStampPrecision = TimeStampPrecision.Millisecond;
            this.EnableLastMsgSeqNumProcessed = false;
            this.MaxMessagesInResendRequest = 0;
            this.SendLogoutBeforeTimeoutDisconnect = false;
            this.IgnorePossDupResendRequests = false;
            this.RequiresOrigSendingTime = true;
            this.CheckLatency = true;
            this.MaxLatency = 120;

            if (!IsSessionTime)
                Reset("Out of SessionTime (Session construction)");
            else if (IsNewSession)
                Reset("New session");

            lock (_sessions)
            {
                _sessions[this.SessionID] = this;
            }

            this.Application.OnCreate(this.SessionID);
            Log.OnEvent("Created session");
        }

        #region Static Methods

        /// <summary>
        /// Looks up a Session by its SessionID
        /// </summary>
        /// <param name="sessionID">the SessionID of the Session</param>
        /// <returns>the Session if found, else returns null</returns>
        public static Session LookupSession(SessionID sessionID)
        {
            lock (_sessions) {
                if (_sessions.TryGetValue(sessionID, out Session result))
                    return result;
            }

            return null;
        }

        /// <summary>
        /// Looks up a Session by its SessionID
        /// </summary>
        /// <param name="sessionID">the SessionID of the Session</param>
        /// <returns>the true if Session exists, false otherwise</returns>
        public static bool DoesSessionExist(SessionID sessionID)
        {
            return LookupSession(sessionID) != null;
        }

        /// <summary>
        /// Sends a message to the session specified by the provider session ID.
        /// </summary>
        /// <param name="message">FIX message</param>
        /// <param name="sessionID">target SessionID</param>
        /// <returns>true if send was successful, false otherwise</returns>
        public static bool SendToTarget(Message message, SessionID sessionID)
        {
            message.SetSessionID(sessionID);
            Session session = Session.LookupSession(sessionID);
            if (null == session)
                throw new SessionNotFound(sessionID);
            return session.Send(message);
        }

        /// <summary>
        /// Send to session indicated by header fields in message
        /// </summary>
        /// <param name="message"></param>
        /// <returns></returns>
        public static bool SendToTarget(Message message)
        {
            SessionID sessionID = message.GetSessionID(message);
            return SendToTarget(message, sessionID);
        }

        #endregion

        /// <summary>
        /// Sends a message via the session indicated by the header fields
        /// </summary>
        /// <param name="message">message to send</param>
        /// <returns>true if was sent successfully</returns>
        public virtual bool Send(Message message)
        {
            message.Header.RemoveField(Fields.Tags.PossDupFlag);
            message.Header.RemoveField(Fields.Tags.OrigSendingTime);
            return SendRaw(message, 0);
        }

        /// <summary>
        /// Sends a message
        /// </summary>
        /// <param name="message"></param>
        /// <returns></returns>
        public bool Send(string message)
        {
            lock (_sync)
            {
                if (null == _responder)
                    return false;
                Log.OnOutgoing(message);
                return _responder.Send(message);
            }
        }

        // TODO for v2 - rename, make internal
        /// <summary>
        /// Sets some internal state variables.  Despite the name, it does not do anything to make a logon occur.
        /// </summary>
        public void Logon()
        {
            _state.IsEnabled = true;
            _state.LogoutReason = "";
        }

        // TODO for v2 - rename, make internal
        /// <summary>
        /// Sets some internal state variables.  Despite the name, it does not cause a logout to occur.
        /// </summary>
        public void Logout()
        {
            Logout("");
        }

        // TODO for v2 - rename, make internal
        /// <summary>
        /// Sets some internal state variables.  Despite the name, it does not cause a logout to occur.
        /// </summary>
        public void Logout(string reason)
        {
            _state.IsEnabled = false;
            _state.LogoutReason = reason;
        }

        /// <summary>
        /// Logs out from session and closes the network connection
        /// </summary>
        /// <param name="reason"></param>
        public void Disconnect(string reason)
        {
            lock (_sync)
            {
                if (_responder is not null)
                {
                    Log.OnEvent("Session " + this.SessionID + " disconnecting: " + reason);
                    _responder.Disconnect();
                    _responder = null;
                }
                else
                {
                    Log.OnEvent("Session " + this.SessionID + " already disconnected: " + reason);
                }

                if (_state.ReceivedLogon || _state.SentLogon)
                {
                    _state.ReceivedLogon = false;
                    _state.SentLogon = false;
                    this.Application.OnLogout(this.SessionID);
                }

                _state.SentLogout = false;
                _state.ReceivedReset = false;
                _state.SentReset = false;
                _state.ClearQueue();
                _state.LogoutReason = "";
                if (this.ResetOnDisconnect)
                    _state.Reset("ResetOnDisconnect");
                _state.SetResendRange(0, 0);
            }
        }

        /// <summary>
        /// There's no message to process, but check the session state to see if there's anything to do
        /// (e.g. send heartbeat, logout at end of session, etc)
        /// </summary>
        public void Next()
        {
            if (!HasResponder)
                return;

            if (!IsSessionTime)
            {
                if(IsInitiator)
                    Reset("Out of SessionTime (Session.Next())");
                else
                    Reset("Out of SessionTime (Session.Next())", "Message received outside of session time");
                return;
            }

            if (IsNewSession)
                _state.Reset("New session (detected in Next())");

            if (!IsEnabled)
            {
                if (!IsLoggedOn)
                    return;

                if (!_state.SentLogout)
                {
                    Log.OnEvent("Initiated logout request");
                    GenerateLogout(_state.LogoutReason);
                }
            }

            if (!_state.ReceivedLogon)
            {
                if (_state.ShouldSendLogon && IsTimeToGenerateLogon())
                {
                    if (GenerateLogon())
                        Log.OnEvent("Initiated logon request");
                    else
                        Log.OnEvent("Error during logon request initiation");

                }
                else if (_state.SentLogon && _state.LogonTimedOut())
                {
                    Disconnect("Timed out waiting for logon response");
                }
                return;
            }

            if (0 == _state.HeartBtInt)
                return;

            if (_state.LogoutTimedOut())
                Disconnect("Timed out waiting for logout response");

            if (_state.WithinHeartbeat())
                return;

            if (_state.TimedOut())
            {
                if (this.SendLogoutBeforeTimeoutDisconnect)
                    GenerateLogout();
                Disconnect("Timed out waiting for heartbeat");
            }
            else
            {
                if (_state.NeedTestRequest())
                {

                    GenerateTestRequest("TEST");
                    _state.TestRequestCounter += 1;
                    Log.OnEvent("Sent test request TEST");
                }
                else if (_state.NeedHeartbeat())
                {
                    GenerateHeartbeat();
                }
            }
        }

        /// <summary>
        /// Process a message (in string form) from the counterparty
        /// </summary>
        /// <param name="msgStr"></param>
        public void Next(string msgStr)
        {
            NextMessage(msgStr);
            NextQueued();
        }

        /// <summary>
        /// Process a message (in string form) from the counterparty
        /// </summary>
        /// <param name="msgStr"></param>
        private void NextMessage(string msgStr)
        {
            Log.OnIncoming(msgStr);

            MessageBuilder msgBuilder = new MessageBuilder(
                    msgStr,
                    SenderDefaultApplVerID,
                    this.ValidateLengthAndChecksum,
                    this.SessionDataDictionary,
                    this.ApplicationDataDictionary,
                    _msgFactory);

            Next(msgBuilder);
        }

        /// <summary>
        /// Process a message from the counterparty.
        /// </summary>
        /// <param name="msgBuilder"></param>
        internal void Next(MessageBuilder msgBuilder)
        {
            if (!IsSessionTime)
            {
                Reset("Out of SessionTime (Session.Next(message))", "Message received outside of session time");
                return;
            }

            if (IsNewSession)
                _state.Reset("New session (detected in Next(Message))");

            Message message = null; // declared outside of try-block so that catch-blocks can use it

            try
            {
                message = msgBuilder.Build();

                if (_appDoesEarlyIntercept)
                    ((IApplicationExt)Application).FromEarlyIntercept(message, this.SessionID);

                Header header = message.Header;
                string msgType = msgBuilder.MsgType.Obj;
                string beginString = msgBuilder.BeginString;

                if (!beginString.Equals(this.SessionID.BeginString))
                    throw new UnsupportedVersion(beginString);


                if (MsgType.LOGON.Equals(msgType)) {
                    targetDefaultApplVerID = this.SessionID.IsFIXT
                        ? new ApplVerID(message.GetString(Fields.Tags.DefaultApplVerID))
                        : Message.GetApplVerID(beginString);
                }

                if (this.SessionID.IsFIXT && !Message.IsAdminMsgType(msgType))
                {
                    DataDictionary.DataDictionary.Validate(message, SessionDataDictionary, ApplicationDataDictionary, beginString, msgType);
                }
                else
                {
                    this.SessionDataDictionary.Validate(message, beginString, msgType);
                }


                if (MsgType.LOGON.Equals(msgType))
                    NextLogon(message);
                else if (MsgType.LOGOUT.Equals(msgType))
                    NextLogout(message);
                else if (!IsLoggedOn)
                    Disconnect($"Received msg type '{msgType}' when not logged on");
                else if (MsgType.HEARTBEAT.Equals(msgType))
                    NextHeartbeat(message);
                else if (MsgType.TEST_REQUEST.Equals(msgType))
                    NextTestRequest(message);
                else if (MsgType.SEQUENCE_RESET.Equals(msgType))
                    NextSequenceReset(message);
                else if (MsgType.RESEND_REQUEST.Equals(msgType))
                    NextResendRequest(message);
                else
                {
                    if (!Verify(message))
                        return;
                    _state.IncrNextTargetMsgSeqNum();
                }

            }
            catch (InvalidMessage e)
            {
                Log.OnEvent(e.Message);

                try
                {
                    if (MsgType.LOGON.Equals(msgBuilder.MsgType.Obj))
                        Disconnect("Logon message is not valid");
                }
                catch (MessageParseError)
                { }

                throw;
            }
            catch (TagException e)
            {
                if (e.InnerException is not null)
                    Log.OnEvent(e.InnerException.Message);
                GenerateReject(msgBuilder, e.sessionRejectReason, e.Field);
            }
            catch (UnsupportedVersion uvx)
            {
                if (MsgType.LOGOUT.Equals(msgBuilder.MsgType.Obj))
                {
                    NextLogout(message);
                }
                else
                {
                    Log.OnEvent(uvx.ToString());
                    GenerateLogout(uvx.Message);
                    _state.IncrNextTargetMsgSeqNum();
                }
            }
            catch (UnsupportedMessageType e)
            {
                Log.OnEvent("Unsupported message type: " + e.Message);
                GenerateBusinessMessageReject(message, Fields.BusinessRejectReason.UNKNOWN_MESSAGE_TYPE, 0);
            }
            catch (FieldNotFoundException e)
            {
                Log.OnEvent("Rejecting invalid message, field not found: " + e.Message);
                if ((string.CompareOrdinal(SessionID.BeginString, FixValues.BeginString.FIX42) >= 0) && (message.IsApp()))
                {
                    GenerateBusinessMessageReject(message, Fields.BusinessRejectReason.CONDITIONALLY_REQUIRED_FIELD_MISSING, e.Field);
                }
                else
                {
                    if (MsgType.LOGON.Equals(msgBuilder.MsgType.Obj))
                    {
                        Log.OnEvent("Required field missing from logon");
                        Disconnect("Required field missing from logon");
                    }
                    else
                        GenerateReject(msgBuilder, new QuickFix.FixValues.SessionRejectReason(SessionRejectReason.REQUIRED_TAG_MISSING, "Required Tag Missing"), e.Field);
                }
            }
            catch (RejectLogon e)
            {
                GenerateLogout(e.Message);
                Disconnect(e.ToString());
            }

            Next();
        }

        protected void NextLogon(Message logon)
        {
            Fields.ResetSeqNumFlag resetSeqNumFlag = new Fields.ResetSeqNumFlag(false);
            if (logon.IsSetField(resetSeqNumFlag))
                logon.GetField(resetSeqNumFlag);
            _state.ReceivedReset = resetSeqNumFlag.Obj;

            if (_state.ReceivedReset)
            {
                Log.OnEvent("Sequence numbers reset due to ResetSeqNumFlag=Y");
                if (!_state.SentReset)
                {
                    _state.Reset("Reset requested by counterparty");
                }
            }

            if (IsAcceptor && this.ResetOnLogon)
                _state.Reset("ResetOnLogon");
            if (this.RefreshOnLogon)
                Refresh();

            if (!Verify(logon, false, true))
                return;

            if (!IsGoodTime(logon))
            {
                Log.OnEvent("Logon has bad sending time");
                Disconnect("bad sending time");
                return;
            }

            _state.ReceivedLogon = true;
            Log.OnEvent("Received logon");
            if (IsAcceptor)
            {
                int heartBtInt = logon.GetInt(Fields.Tags.HeartBtInt);
                _state.HeartBtInt = heartBtInt;
                GenerateLogon(logon);
                Log.OnEvent($"Responding to logon request; heartbeat is {heartBtInt} seconds");
            }

            _state.SentReset = false;
            _state.ReceivedReset = false;

            SeqNumType msgSeqNum = logon.Header.GetULong(Fields.Tags.MsgSeqNum);
            if (IsTargetTooHigh(msgSeqNum) && !resetSeqNumFlag.Obj)
            {
                DoTargetTooHigh(logon, msgSeqNum);
            }
            else
            {
                _state.IncrNextTargetMsgSeqNum();
            }

            if (this.IsLoggedOn)
                this.Application.OnLogon(this.SessionID);
        }

        protected void NextTestRequest(Message testRequest)
        {
            if (!Verify(testRequest))
                return;
            GenerateHeartbeat(testRequest);
            _state.IncrNextTargetMsgSeqNum();
        }

        protected void NextResendRequest(Message resendReq)
        {
            if (!Verify(resendReq, false, false))
                return;
            try
            {
                SeqNumType msgSeqNum = 0;
                if (!(this.IgnorePossDupResendRequests && resendReq.Header.IsSetField(Tags.PossDupFlag)))
                {
                    SeqNumType begSeqNo = resendReq.GetULong(Fields.Tags.BeginSeqNo);
                    SeqNumType endSeqNo = resendReq.GetULong(Fields.Tags.EndSeqNo);
                    Log.OnEvent("Got resend request from " + begSeqNo + " to " + endSeqNo);

                    if ((endSeqNo == 999999) || (endSeqNo == 0))
                    {
                        endSeqNo = _state.NextSenderMsgSeqNum - 1;
                    }

                    if (!PersistMessages)
                    {
                        endSeqNo++;
                        SeqNumType next = _state.NextSenderMsgSeqNum;
                        if (endSeqNo > next)
                            endSeqNo = next;
                        GenerateSequenceReset(resendReq, begSeqNo, endSeqNo);
                        msgSeqNum = resendReq.Header.GetULong(Tags.MsgSeqNum);
                        if (!IsTargetTooHigh(msgSeqNum) && !IsTargetTooLow(msgSeqNum))
                        {
                            _state.IncrNextTargetMsgSeqNum();
                        }
                        return;
                    }

                    List<string> messages = new List<string>();
                    _state.Get(begSeqNo, endSeqNo, messages);
                    SeqNumType current = begSeqNo;
                    SeqNumType begin = 0;
                    foreach (string msgStr in messages)
                    {
                        Message msg = new Message();
                        msg.FromString(msgStr, true, this.SessionDataDictionary, this.ApplicationDataDictionary, _msgFactory, ignoreBody: false);
                        msgSeqNum = msg.Header.GetULong(Tags.MsgSeqNum);

                        if ((current != msgSeqNum) && begin == 0)
                        {
                            begin = current;
                        }

                        if (IsAdminMessage(msg) && !(this.ResendSessionLevelRejects && msg.Header.GetString(Tags.MsgType) == MsgType.REJECT))
                        {
                            if (begin == 0)
                            {
                                begin = msgSeqNum;
                            }
                        }
                        else
                        {

                            initializeResendFields(msg);
                            if(!ResendApproved(msg, SessionID))
                            {
                                continue;
                            }

                            if (begin != 0)
                            {
                                GenerateSequenceReset(resendReq, begin, msgSeqNum);
                            }
                            Send(msg.ToString());
                            begin = 0;
                        }
                        current = msgSeqNum + 1;
                    }

                    SeqNumType nextSeqNum = _state.NextSenderMsgSeqNum;
                    if (++endSeqNo > nextSeqNum)
                    {
                        endSeqNo = nextSeqNum;
                    }

                    if (begin == 0)
                    {
                        begin = current;
                    }

                    if (endSeqNo > begin)
                    {
                        GenerateSequenceReset(resendReq, begin, endSeqNo);
                    }
                }
                msgSeqNum = resendReq.Header.GetULong(Tags.MsgSeqNum);
                if (!IsTargetTooHigh(msgSeqNum) && !IsTargetTooLow(msgSeqNum))
                {
                    _state.IncrNextTargetMsgSeqNum();
                }

            }
            catch (Exception e)
            {
                Log.OnEvent("ERROR during resend request " + e.Message);
            }
        }
        private bool ResendApproved(Message msg, SessionID sessionID)
        {
            try
            {
                Application.ToApp(msg, sessionID);
            }
            catch (DoNotSend)
            {
                return false;
            }

            return true;
        }

        protected void NextLogout(Message logout)
        {
            if (!Verify(logout, false, false))
                return;

            string disconnectReason;

            if (!_state.SentLogout)
            {
                disconnectReason = "Received logout request";
                Log.OnEvent(disconnectReason);
                GenerateLogout(logout);
                Log.OnEvent("Sending logout response");
            }
            else
            {
                disconnectReason = "Received logout response";
                Log.OnEvent(disconnectReason);
            }

            _state.IncrNextTargetMsgSeqNum();
            if (this.ResetOnLogout)
                _state.Reset("ResetOnLogout");
            Disconnect(disconnectReason);
        }

        protected void NextHeartbeat(Message heartbeat)
        {
            if (!Verify(heartbeat))
                return;
            _state.IncrNextTargetMsgSeqNum();
        }

        protected void NextSequenceReset(Message sequenceReset)
        {
            bool isGapFill = false;
            if (sequenceReset.IsSetField(Fields.Tags.GapFillFlag))
                isGapFill = sequenceReset.GetBoolean(Fields.Tags.GapFillFlag);

            if (!Verify(sequenceReset, isGapFill, isGapFill))
                return;

            if (sequenceReset.IsSetField(Fields.Tags.NewSeqNo))
            {
                SeqNumType newSeqNo = sequenceReset.GetULong(Fields.Tags.NewSeqNo);
                Log.OnEvent("Received SequenceReset FROM: " + _state.NextTargetMsgSeqNum + " TO: " + newSeqNo);

                if (newSeqNo > _state.NextTargetMsgSeqNum)
                {
                    _state.NextTargetMsgSeqNum = newSeqNo;
                }
                else
                {
                    if (newSeqNo < _state.NextTargetMsgSeqNum)
                        GenerateReject(sequenceReset, FixValues.SessionRejectReason.VALUE_IS_INCORRECT);
                }
            }
        }

        public bool Verify(Message message)
        {
            return Verify(message, true, true);
        }

        public bool Verify(Message msg, bool checkTooHigh, bool checkTooLow)
        {
            SeqNumType msgSeqNum = 0;
            string msgType = "";

            try
            {
                msgType = msg.Header.GetString(Fields.Tags.MsgType);
                string senderCompID = msg.Header.GetString(Fields.Tags.SenderCompID);
                string targetCompID = msg.Header.GetString(Fields.Tags.TargetCompID);

                if (!IsCorrectCompID(senderCompID, targetCompID))
                {
                    GenerateReject(msg, FixValues.SessionRejectReason.COMPID_PROBLEM);
                    GenerateLogout();
                    return false;
                }

                if (checkTooHigh || checkTooLow)
                    msgSeqNum = msg.Header.GetULong(Fields.Tags.MsgSeqNum);

                if (checkTooHigh && IsTargetTooHigh(msgSeqNum))
                {
                    DoTargetTooHigh(msg, msgSeqNum);
                    return false;
                }
                else if (checkTooLow && IsTargetTooLow(msgSeqNum))
                {
                    DoTargetTooLow(msg, msgSeqNum);
                    return false;
                }

                if ((checkTooHigh || checkTooLow) && _state.ResendRequested())
                {
                    ResendRange range = _state.GetResendRange();
                    if (msgSeqNum >= range.EndSeqNo)
                    {
                        Log.OnEvent("ResendRequest for messages FROM: " + range.BeginSeqNo + " TO: " + range.EndSeqNo + " has been satisfied.");
                        _state.SetResendRange(0, 0);
                    }
                    else if (msgSeqNum >= range.ChunkEndSeqNo)
                    {
                        Log.OnEvent("Chunked ResendRequest for messages FROM: " + range.BeginSeqNo + " TO: " + range.ChunkEndSeqNo + " has been satisfied.");
                        SeqNumType newChunkEndSeqNo = Math.Min(range.EndSeqNo, range.ChunkEndSeqNo + this.MaxMessagesInResendRequest);
                        GenerateResendRequestRange(msg.Header.GetString(Fields.Tags.BeginString), range.ChunkEndSeqNo + 1, newChunkEndSeqNo);
                        range.ChunkEndSeqNo = newChunkEndSeqNo;
                    }
                }

                if (!IsGoodTime(msg))
                {
                    Log.OnEvent("Sending time accuracy problem");
                    GenerateReject(msg, FixValues.SessionRejectReason.SENDING_TIME_ACCURACY_PROBLEM);
                    GenerateLogout();
                    return false;
                }
            }
            catch (System.Exception e)
            {
                Log.OnEvent("Verify failed: " + e.Message);
                Disconnect("Verify failed: " + e.Message);
                return false;
            }

            _state.LastReceivedTimeDT = DateTime.UtcNow;
            _state.TestRequestCounter = 0;

            if (Message.IsAdminMsgType(msgType))
                this.Application.FromAdmin(msg, this.SessionID);
            else
                this.Application.FromApp(msg, this.SessionID);

            return true;
        }

        public void SetResponder(IResponder responder)
        {
            if (!IsSessionTime)
                Reset("Out of SessionTime (Session.SetResponder)");

            lock (_sync)
            {
                _responder = responder;
            }
        }

        public void Refresh()
        {
            _state.Refresh();
        }

        /// <summary>
        /// Send a logout, disconnect, and reset session state
        /// </summary>
        /// <param name="loggedReason">reason for the reset (for the log)</param>
        public void Reset(string loggedReason)
        {
            Reset(loggedReason, null);
        }

        /// <summary>
        /// Send a logout, disconnect, and reset session state
        /// </summary>
        /// <param name="loggedReason">reason for the reset (for the log)</param>
        /// <param name="logoutMessage">message to put in the Logout message's Text field (ignored if null/empty string)</param>
        public void Reset(string loggedReason, string logoutMessage)
        {
            if(this.IsLoggedOn)
                GenerateLogout(logoutMessage);
            Disconnect("Resetting...");
            _state.Reset(loggedReason);
        }

        private void initializeResendFields(Message message)
        {
            FieldMap header = message.Header;
            System.DateTime sendingTime = header.GetDateTime(Fields.Tags.SendingTime);
            InsertOrigSendingTime(header, sendingTime);
            header.SetField(new Fields.PossDupFlag(true));
            InsertSendingTime(header);
        }

        protected bool ShouldSendReset()
        {
            return (string.CompareOrdinal(SessionID.BeginString, FixValues.BeginString.FIX41) >= 0)
                && (this.ResetOnLogon || this.ResetOnLogout || this.ResetOnDisconnect)
                && (_state.NextSenderMsgSeqNum == 1)
                && (_state.NextTargetMsgSeqNum == 1);
        }

        protected bool IsCorrectCompID(string senderCompID, string targetCompID)
        {
            if (!this.CheckCompID)
                return true;
            return this.SessionID.SenderCompID.Equals(targetCompID)
                && this.SessionID.TargetCompID.Equals(senderCompID);
        }

        /// FIXME - this fn always returns true-- should it ever be false?
        protected bool IsTimeToGenerateLogon()
        {
            return true;
        }

        protected bool IsTargetTooHigh(SeqNumType msgSeqNum)
        {
            return msgSeqNum > _state.NextTargetMsgSeqNum;
        }

        protected bool IsTargetTooLow(SeqNumType msgSeqNum)
        {
            return msgSeqNum < _state.NextTargetMsgSeqNum;
        }

        protected void DoTargetTooHigh(Message msg, SeqNumType msgSeqNum)
        {
            string beginString = msg.Header.GetString(Fields.Tags.BeginString);

            Log.OnEvent("MsgSeqNum too high, expecting " + _state.NextTargetMsgSeqNum + " but received " + msgSeqNum);
            _state.Queue(msgSeqNum, msg);

            if (_state.ResendRequested())
            {
                ResendRange range = _state.GetResendRange();

                if (!this.SendRedundantResendRequests && msgSeqNum >= range.BeginSeqNo)
                {
                    Log.OnEvent("Already sent ResendRequest FROM: " + range.BeginSeqNo + " TO: " + range.EndSeqNo + ".  Not sending another.");
                    return;
                }
            }

            GenerateResendRequest(beginString, msgSeqNum);
        }

        protected void DoTargetTooLow(Message msg, SeqNumType msgSeqNum)
        {
            bool possDupFlag = false;
            if (msg.Header.IsSetField(Fields.Tags.PossDupFlag))
                possDupFlag = msg.Header.GetBoolean(Fields.Tags.PossDupFlag);

            if (!possDupFlag)
            {
                string err = "MsgSeqNum too low, expecting " + _state.NextTargetMsgSeqNum + " but received " + msgSeqNum;
                GenerateLogout(err);
                throw new QuickFIXException(err);
            }

            DoPossDup(msg);
        }

        /// <summary>
        /// Validates a message where PossDupFlag=Y
        /// </summary>
        /// <param name="msg"></param>
        protected void DoPossDup(Message msg)
        {
            // If config RequiresOrigSendingTime=N, then tolerate SequenceReset messages that lack OrigSendingTime (issue #102).
            // (This field doesn't really make sense in this message, so some parties omit it, even though spec requires it.)
            string msgType = msg.Header.GetString(Fields.Tags.MsgType);
            if (msgType == Fields.MsgType.SEQUENCE_RESET && RequiresOrigSendingTime == false)
                return;

            // Reject if messages don't have OrigSendingTime set
            if (!msg.Header.IsSetField(Fields.Tags.OrigSendingTime))
            {
                GenerateReject(msg, FixValues.SessionRejectReason.REQUIRED_TAG_MISSING, Fields.Tags.OrigSendingTime);
                return;
            }

            // Ensure sendingTime is later than OrigSendingTime, else reject and logout
            DateTime origSendingTime = msg.Header.GetDateTime(Fields.Tags.OrigSendingTime);
            DateTime sendingTime = msg.Header.GetDateTime(Fields.Tags.SendingTime);
            System.TimeSpan tmSpan = origSendingTime - sendingTime;

            if (tmSpan.TotalSeconds > 0)
            {
                GenerateReject(msg, FixValues.SessionRejectReason.SENDING_TIME_ACCURACY_PROBLEM);
                GenerateLogout();
            }
        }

        protected void GenerateBusinessMessageReject(Message message, int err, int field)
        {
            string msgType = message.Header.GetString(Tags.MsgType);
            SeqNumType msgSeqNum = message.Header.GetULong(Tags.MsgSeqNum);
            string reason = FixValues.BusinessRejectReason.RejText[err];
            Message reject;
            if (string.CompareOrdinal(SessionID.BeginString, FixValues.BeginString.FIX42) >= 0)
            {
                reject = _msgFactory.Create(this.SessionID.BeginString, MsgType.BUSINESS_MESSAGE_REJECT);
                reject.SetField(new RefMsgType(msgType));
                reject.SetField(new BusinessRejectReason(err));
            }
            else
            {
                reject = _msgFactory.Create(this.SessionID.BeginString, MsgType.REJECT);
                char[] reasonArray = reason.ToLower().ToCharArray();
                reasonArray[0] = char.ToUpper(reasonArray[0]);
                reason = new string(reasonArray);
            }
            InitializeHeader(reject);
            reject.SetField(new RefSeqNum(msgSeqNum));
            _state.IncrNextTargetMsgSeqNum();


            reject.SetField(new Text(reason));
            Log.OnEvent("Reject sent for Message: " + msgSeqNum + " Reason:" + reason);
            SendRaw(reject, 0);
        }

        protected bool GenerateResendRequestRange(string beginString, SeqNumType startSeqNum, SeqNumType endSeqNum)
        {
            Message resendRequest = _msgFactory.Create(beginString, MsgType.RESEND_REQUEST);

            resendRequest.SetField(new Fields.BeginSeqNo(startSeqNum));
            resendRequest.SetField(new Fields.EndSeqNo(endSeqNum));

            InitializeHeader(resendRequest);
            if (SendRaw(resendRequest, 0))
            {
                Log.OnEvent("Sent ResendRequest FROM: " + startSeqNum + " TO: " + endSeqNum);
                return true;
            }
            else
            {
                Log.OnEvent("Error sending ResendRequest (" + startSeqNum + " ," + endSeqNum + ")");
                return false;
            }
        }

        protected void GenerateResendRequest(string beginString, SeqNumType msgSeqNum)
        {
            SeqNumType beginSeqNum = _state.NextTargetMsgSeqNum;
            SeqNumType endRangeSeqNum = msgSeqNum - 1;
            SeqNumType endChunkSeqNum;
            if (this.MaxMessagesInResendRequest > 0)
            {
                endChunkSeqNum = Math.Min(endRangeSeqNum, beginSeqNum + this.MaxMessagesInResendRequest - 1);
            }
            else
            {
                if (string.CompareOrdinal(beginString, FixValues.BeginString.FIX42) >= 0)
                    endRangeSeqNum = 0;
                else if (string.CompareOrdinal(beginString, FixValues.BeginString.FIX41) <= 0)
                    endRangeSeqNum = 999999;
                endChunkSeqNum = endRangeSeqNum;
            }

            if (!GenerateResendRequestRange(beginString, beginSeqNum, endChunkSeqNum)) {
                return;
            }

            _state.SetResendRange(beginSeqNum, endRangeSeqNum, endChunkSeqNum);
        }

        /// <summary>
        /// FIXME
        /// </summary>
        /// <returns></returns>
        protected bool GenerateLogon()
        {
            Message logon = _msgFactory.Create(this.SessionID.BeginString, Fields.MsgType.LOGON);
            logon.SetField(new Fields.EncryptMethod(0));
            logon.SetField(new Fields.HeartBtInt(_state.HeartBtInt));

            if (this.SessionID.IsFIXT)
                logon.SetField(new Fields.DefaultApplVerID(this.SenderDefaultApplVerID));
            if (this.RefreshOnLogon)
                Refresh();
            if (this.ResetOnLogon)
                _state.Reset("ResetOnLogon");
            if (ShouldSendReset())
                logon.SetField(new Fields.ResetSeqNumFlag(true));

            InitializeHeader(logon);
            _state.LastReceivedTimeDT = DateTime.UtcNow;
            _state.TestRequestCounter = 0;
            _state.SentLogon = true;
            return SendRaw(logon, 0);
        }

        /// <summary>
        /// FIXME don't do so much operator new here
        /// </summary>
        /// <param name="otherLogon"></param>
        /// <returns></returns>
        protected bool GenerateLogon(Message otherLogon)
        {
            Message logon = _msgFactory.Create(this.SessionID.BeginString, Fields.MsgType.LOGON);
            logon.SetField(new Fields.EncryptMethod(0));
            if (this.SessionID.IsFIXT)
                logon.SetField(new Fields.DefaultApplVerID(this.SenderDefaultApplVerID));
            logon.SetField(new Fields.HeartBtInt(otherLogon.GetInt(Tags.HeartBtInt)));
            if (this.EnableLastMsgSeqNumProcessed)
                logon.Header.SetField(new Fields.LastMsgSeqNumProcessed(otherLogon.Header.GetULong(Tags.MsgSeqNum)));

            InitializeHeader(logon);
            _state.SentLogon = SendRaw(logon, 0);
            return _state.SentLogon;
        }

        public void GenerateTestRequest(string id)
        {
            Message testRequest = _msgFactory.Create(this.SessionID.BeginString, Fields.MsgType.TEST_REQUEST);
            InitializeHeader(testRequest);
            testRequest.SetField(new Fields.TestReqID(id));
            SendRaw(testRequest, 0);
        }

        /// <summary>
        /// Send a basic Logout message
        /// </summary>
        /// <returns></returns>
        public void GenerateLogout()
        {
            GenerateLogout(null, null);
        }

        /// <summary>
        /// Send a Logout message
        /// </summary>
        /// <param name="text">written into the Text field</param>
        /// <returns></returns>
        private void GenerateLogout(string text)
        {
            GenerateLogout(null, text);
        }

        /// <summary>
        /// Send a Logout message
        /// </summary>
        /// <param name="other">used to fill MsgSeqNum field, if configuration requires it</param>
        /// <returns></returns>
        private void GenerateLogout(Message other)
        {
            GenerateLogout(other, null);
        }

        /// <summary>
        /// Send a Logout message
        /// </summary>
        /// <param name="other">used to fill MsgSeqNum field, if configuration requires it; ignored if null</param>
        /// <param name="text">written into the Text field; ignored if empty/null</param>
        /// <returns></returns>
        private void GenerateLogout(Message other, string text)
        {
            Message logout = _msgFactory.Create(this.SessionID.BeginString, Fields.MsgType.LOGOUT);
            InitializeHeader(logout);
            if (text != null && text.Length > 0)
                logout.SetField(new Fields.Text(text));
            if (other != null && this.EnableLastMsgSeqNumProcessed)
            {
                try
                {
                    logout.Header.SetField(new Fields.LastMsgSeqNumProcessed(other.Header.GetULong(Tags.MsgSeqNum)));
                }
                catch (FieldNotFoundException)
                {
                    Log.OnEvent("Error: No message sequence number: " + other);
                }
            }
            _state.SentLogout = SendRaw(logout, 0);
        }

        public void GenerateHeartbeat()
        {
            Message heartbeat = _msgFactory.Create(this.SessionID.BeginString, Fields.MsgType.HEARTBEAT);
            InitializeHeader(heartbeat);
            SendRaw(heartbeat, 0);
        }

        public void GenerateHeartbeat(Message testRequest)
        {
            Message heartbeat = _msgFactory.Create(this.SessionID.BeginString, Fields.MsgType.HEARTBEAT);
            InitializeHeader(heartbeat);
            try
            {
                heartbeat.SetField(new Fields.TestReqID(testRequest.GetString(Fields.Tags.TestReqID)));
                if (this.EnableLastMsgSeqNumProcessed)
                {
                    heartbeat.Header.SetField(new Fields.LastMsgSeqNumProcessed(testRequest.Header.GetULong(Tags.MsgSeqNum)));
                }
            }
            catch (FieldNotFoundException)
            { }
            SendRaw(heartbeat, 0);
        }


        internal void GenerateReject(MessageBuilder msgBuilder, FixValues.SessionRejectReason reason)
        {
            GenerateReject(msgBuilder.RejectableMessage(), reason, 0);
        }

        internal void GenerateReject(MessageBuilder msgBuilder, FixValues.SessionRejectReason reason, int field)
        {
            GenerateReject(msgBuilder.RejectableMessage(), reason, field);
        }

        public void GenerateReject(Message message, FixValues.SessionRejectReason reason)
        {
            GenerateReject(message, reason, 0);
        }

        public void GenerateReject(Message message, FixValues.SessionRejectReason reason, int field)
        {
            string beginString = this.SessionID.BeginString;

            Message reject = _msgFactory.Create(beginString, Fields.MsgType.REJECT);
            reject.ReverseRoute(message.Header);
            InitializeHeader(reject);

            string msgType;
            if (message.Header.IsSetField(Fields.Tags.MsgType))
                msgType = message.Header.GetString(Fields.Tags.MsgType);
            else
                msgType = "";

            SeqNumType msgSeqNum = 0;
            if (message.Header.IsSetField(Fields.Tags.MsgSeqNum))
            {
                try
                {
                    msgSeqNum = message.Header.GetULong(Fields.Tags.MsgSeqNum);
                    reject.SetField(new Fields.RefSeqNum(msgSeqNum));
                }
                catch (Exception)
                { }
            }

            if (string.CompareOrdinal(beginString, FixValues.BeginString.FIX42) >= 0)
            {
                if (msgType.Length > 0)
                    reject.SetField(new Fields.RefMsgType(msgType));
                if ((FixValues.BeginString.FIX42.Equals(beginString) && reason.Value <= FixValues.SessionRejectReason.INVALID_MSGTYPE.Value) || (string.CompareOrdinal(beginString, FixValues.BeginString.FIX42) > 0))
                {
                    reject.SetField(new Fields.SessionRejectReason(reason.Value));
                }
            }
            if (!MsgType.LOGON.Equals(msgType)
              && !MsgType.SEQUENCE_RESET.Equals(msgType)
              && (msgSeqNum == _state.NextTargetMsgSeqNum))
            {
                _state.IncrNextTargetMsgSeqNum();
            }

            if ((0 != field) || FixValues.SessionRejectReason.INVALID_TAG_NUMBER.Equals(reason))
            {
                if (FixValues.SessionRejectReason.INVALID_MSGTYPE.Equals(reason))
                {
                    if (string.CompareOrdinal(SessionID.BeginString, FixValues.BeginString.FIX43) >= 0)
                        PopulateRejectReason(reject, reason.Description);
                    else
                        PopulateSessionRejectReason(reject, field, reason.Description, false);
                }
                else
                    PopulateSessionRejectReason(reject, field, reason.Description, true);

                Log.OnEvent("Message " + msgSeqNum + " Rejected: " + reason.Description + " (Field=" + field + ")");
            }
            else
            {
                PopulateRejectReason(reject, reason.Description);
                Log.OnEvent("Message " + msgSeqNum + " Rejected: " + reason.Value);
            }

            if (!_state.ReceivedLogon)
                throw new QuickFIXException("Tried to send a reject while not logged on");

            SendRaw(reject, 0);
        }

        protected void PopulateSessionRejectReason(Message reject, int field, string text, bool includeFieldInfo)
        {
            if (string.CompareOrdinal(SessionID.BeginString, FixValues.BeginString.FIX42) >= 0)
            {
                reject.SetField(new Fields.RefTagID(field));
                reject.SetField(new Fields.Text(text));
            }
            else
            {
                if (includeFieldInfo)
                    reject.SetField(new Fields.Text(text + " (" + field + ")"));
                else
                    reject.SetField(new Fields.Text(text));
            }
        }

        protected void PopulateRejectReason(Message reject, string text)
        {
            reject.SetField(new Fields.Text(text));
        }

        /// <summary>
        /// FIXME don't do so much operator new here
        /// </summary>
        /// <param name="m"></param>
        /// <param name="msgSeqNum"></param>
        protected void InitializeHeader(Message m, SeqNumType msgSeqNum)
        {
            _state.LastSentTimeDT = DateTime.UtcNow;
            m.Header.SetField(new Fields.BeginString(this.SessionID.BeginString));
            m.Header.SetField(new Fields.SenderCompID(this.SessionID.SenderCompID));
            if (SessionID.IsSet(this.SessionID.SenderSubID))
                m.Header.SetField(new Fields.SenderSubID(this.SessionID.SenderSubID));
            if (SessionID.IsSet(this.SessionID.SenderLocationID))
                m.Header.SetField(new Fields.SenderLocationID(this.SessionID.SenderLocationID));
            m.Header.SetField(new Fields.TargetCompID(this.SessionID.TargetCompID));
            if (SessionID.IsSet(this.SessionID.TargetSubID))
                m.Header.SetField(new Fields.TargetSubID(this.SessionID.TargetSubID));
            if (SessionID.IsSet(this.SessionID.TargetLocationID))
                m.Header.SetField(new Fields.TargetLocationID(this.SessionID.TargetLocationID));

            if (msgSeqNum > 0)
                m.Header.SetField(new Fields.MsgSeqNum(msgSeqNum));
            else
                m.Header.SetField(new Fields.MsgSeqNum(_state.NextSenderMsgSeqNum));

            if (this.EnableLastMsgSeqNumProcessed && !m.Header.IsSetField(Tags.LastMsgSeqNumProcessed))
            {
                m.Header.SetField(new LastMsgSeqNumProcessed(this.NextTargetMsgSeqNum - 1));
            }

            InsertSendingTime(m.Header);
        }
        protected void InitializeHeader(Message m)
        {
            InitializeHeader(m, 0);
        }

        private bool IsFix42OrAbove() {
            return this.SessionID.BeginString == FixValues.BeginString.FIXT11
                || string.CompareOrdinal(SessionID.BeginString, FixValues.BeginString.FIX42) >= 0;
        }

        protected void InsertSendingTime(FieldMap header)
        {
            header.SetField(new Fields.SendingTime(
                System.DateTime.UtcNow, IsFix42OrAbove() ? TimeStampPrecision : TimeStampPrecision.Second ) );
        }

        protected void Persist(Message message, string messageString)
        {
            if (this.PersistMessages)
            {
                SeqNumType msgSeqNum = message.Header.GetULong(Fields.Tags.MsgSeqNum);
                _state.Set(msgSeqNum, messageString);
            }
            _state.IncrNextSenderMsgSeqNum();
        }

        protected bool IsGoodTime(Message msg)
        {
            if (!CheckLatency)
                return true;

            var sendingTime = msg.Header.GetDateTime(Fields.Tags.SendingTime);
            System.TimeSpan tmSpan = System.DateTime.UtcNow - sendingTime;
            return System.Math.Abs(tmSpan.TotalSeconds) <= MaxLatency;
        }

        private void GenerateSequenceReset(Message receivedMessage, SeqNumType beginSeqNo, SeqNumType endSeqNo)
        {
            string beginString = this.SessionID.BeginString;
            Message sequenceReset = _msgFactory.Create(beginString, Fields.MsgType.SEQUENCE_RESET);
            InitializeHeader(sequenceReset);
            SeqNumType newSeqNo = endSeqNo;
            sequenceReset.Header.SetField(new PossDupFlag(true));
            InsertOrigSendingTime(sequenceReset.Header, sequenceReset.Header.GetDateTime(Tags.SendingTime));

            sequenceReset.Header.SetField(new MsgSeqNum(beginSeqNo));
            sequenceReset.SetField(new NewSeqNo(newSeqNo));
            sequenceReset.SetField(new GapFillFlag(true));
            if (receivedMessage != null && this.EnableLastMsgSeqNumProcessed)
            {
                try
                {
                    sequenceReset.Header.SetField(new Fields.LastMsgSeqNumProcessed(receivedMessage.Header.GetULong(Tags.MsgSeqNum)));
                }
                catch (FieldNotFoundException)
                {
                    Log.OnEvent("Error: Received message without MsgSeqNum: " + receivedMessage);
                }
            }
            SendRaw(sequenceReset, beginSeqNo);
            Log.OnEvent("Sent SequenceReset TO: " + newSeqNo);
        }

        protected void InsertOrigSendingTime(FieldMap header, System.DateTime sendingTime)
        {
            header.SetField(new OrigSendingTime(
                sendingTime, IsFix42OrAbove() ? TimeStampPrecision : TimeStampPrecision.Second ) );
        }

        protected void NextQueued()
        {
            while (NextQueued(_state.MessageStore.NextTargetMsgSeqNum))
            {
                // continue
            }
        }

        protected bool NextQueued(SeqNumType num)
        {
            Message msg = _state.Dequeue(num);

            if (msg is not null)
            {
                Log.OnEvent("Processing queued message: " + num);

                string msgType = msg.Header.GetString(Tags.MsgType);
                if (msgType.Equals(MsgType.LOGON) || msgType.Equals(MsgType.RESEND_REQUEST))
                {
                    _state.IncrNextTargetMsgSeqNum();
                }
                else
                {
                    NextMessage(msg.ToString());
                }
                return true;
            }
            return false;
        }

        private bool IsAdminMessage(Message msg)
        {
            var msgType = msg.Header.GetString(Fields.Tags.MsgType);
            return AdminMsgTypes.Contains(msgType);
        }

        protected bool SendRaw(Message message, SeqNumType seqNum)
        {
            lock (_sync)
            {
                string msgType = message.Header.GetString(Fields.Tags.MsgType);

                InitializeHeader(message, seqNum);

                if (Message.IsAdminMsgType(msgType))
                {
                    this.Application.ToAdmin(message, this.SessionID);

                    if (MsgType.LOGON.Equals(msgType) && !_state.ReceivedReset)
                    {
                        Fields.ResetSeqNumFlag resetSeqNumFlag = new QuickFix.Fields.ResetSeqNumFlag(false);
                        if (message.IsSetField(resetSeqNumFlag))
                            message.GetField(resetSeqNumFlag);
                        if (resetSeqNumFlag.getValue())
                        {
                            _state.Reset("ResetSeqNumFlag");
                            message.Header.SetField(new Fields.MsgSeqNum(_state.NextSenderMsgSeqNum));
                        }
                        _state.SentReset = resetSeqNumFlag.Obj;
                    }
                }
                else
                {
                    try
                    {
                        this.Application.ToApp(message, this.SessionID);
                    }
                    catch (DoNotSend)
                    {
                        return false;
                    }
                }

                string messageString = message.ToString();
                if (0 == seqNum)
                    Persist(message, messageString);
                return Send(messageString);
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        public bool Disposed { get; private set; } = false;

        protected virtual void Dispose(bool disposing)
        {
            if (Disposed) return;
            if (disposing)
            {
                if (_state != null) { _state.Dispose(); }
                lock (_sessions)
                {
                    _sessions.Remove(this.SessionID);
                }
            }
            Disposed = true;
        }

        ~Session() => Dispose(false);
    }
}
