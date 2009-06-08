//-----------------------------------------------------------------------------
// Filename: UASInviteTransaction.cs
//
// Description: SIP Transaction that implements UAS (User Agent Server) functionality for
// an INVITE transaction.
// 
// History:
// 21 Nov 2006	Aaron Clauson	Created.
//
// License: 
// This software is licensed under the BSD License http://www.opensource.org/licenses/bsd-license.php
//
// Copyright (c) 2006 Aaron Clauson (aaronc@blueface.ie), Blue Face Ltd, Dublin, Ireland (www.blueface.ie)
// All rights reserved.
//
// Redistribution and use in source and binary forms, with or without modification, are permitted provided that 
// the following conditions are met:
//
// Redistributions of source code must retain the above copyright notice, this list of conditions and the following disclaimer. 
// Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following 
// disclaimer in the documentation and/or other materials provided with the distribution. Neither the name of Blue Face Ltd. 
// nor the names of its contributors may be used to endorse or promote products derived from this software without specific 
// prior written permission. 
//
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, 
// BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. 
// IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, 
// OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, 
// OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, 
// OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE 
// POSSIBILITY OF SUCH DAMAGE.
//-----------------------------------------------------------------------------

using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using SIPSorcery.Sys;
using log4net;

#if UNITTEST
using NUnit.Framework;
#endif

namespace SIPSorcery.SIP
{
	/// <summary>
	/// The server transaction for an INVITE request. This transaction processes incoming calls RECEIVED by the application.
	/// </summary>
	public class UASInviteTransaction : SIPTransaction
    {
        private static string m_sipServerAgent = SIPConstants.SIP_SERVER_STRING;

        public SIPAuthoriseRequestDelegate UASSIPRequestAuthenticate;               // Set this delegate if request authorisation is required.
        public event SIPTransactionCancelledDelegate UASInviteTransactionCancelled;
        public event SIPTransactionRequestReceivedDelegate NewCallReceived;
        public event SIPTransactionTimedOutDelegate UASInviteTransactionTimedOut;

        private SIPRequestAuthorisationResult m_authorisationResult;

        public SIPRequestAuthorisationResult AuthorisationResult
        {
            get { return m_authorisationResult; }
        }

        internal UASInviteTransaction(
            SIPTransport sipTransport,
            SIPRequest sipRequest,
            SIPEndPoint dstEndPoint,
            SIPEndPoint localSIPEndPoint,
            SIPEndPoint outboundProxy)
            : base(sipTransport, sipRequest, dstEndPoint, localSIPEndPoint, outboundProxy)
		{
            TransactionType = SIPTransactionTypesEnum.Invite;
            m_remoteTag = sipRequest.Header.From.FromTag;

            if (sipRequest.Header.To.ToTag == null)
            {
                // This UAS needs to set the To Tag.
                m_localTag = CallProperties.CreateNewTag();
            }
            else
            {
                // This is a re-INVITE.
                m_localTag = sipRequest.Header.To.ToTag;
            }

            //logger.Debug("New UASTransaction (" + TransactionId + ") for " + TransactionRequest.URI.ToString() + " to " + RemoteEndPoint + ".");

            CDR = new SIPCDR(SIPCallDirection.In, sipRequest.URI, sipRequest.Header.From, sipRequest.Header.CallId, LocalSIPEndPoint, dstEndPoint);

            //UpdateTransactionState(SIPTransactionStatesEnum.Proceeding);

            TransactionRequestReceived += UASInviteTransaction_TransactionRequestReceived;
            TransactionInformationResponseReceived += UASInviteTransaction_TransactionResponseReceived;
            TransactionFinalResponseReceived += UASInviteTransaction_TransactionResponseReceived;
            TransactionTimedOut += UASInviteTransaction_TransactionTimedOut;
            TransactionRemoved += UASInviteTransaction_TransactionRemoved;
        }

        private void UASInviteTransaction_TransactionRemoved(SIPTransaction transaction)
        {
            // Remove event handlers.
            UASInviteTransactionCancelled = null;
            UASSIPRequestAuthenticate = null;
            NewCallReceived = null;
            UASInviteTransactionTimedOut = null;
            CDR = null;
        }

        private void UASInviteTransaction_TransactionTimedOut(SIPTransaction sipTransaction)
        {
            if (UASInviteTransactionTimedOut != null)
            {
                UASInviteTransactionTimedOut(this);
            }

            if (CDR != null)
            {
                CDR.TimedOut();
            }
        }

        private void UASInviteTransaction_TransactionResponseReceived(SIPEndPoint localSIPEndPoint, SIPEndPoint remoteEndPoint, SIPTransaction sipTransaction, SIPResponse sipResponse)
        {
            logger.Warn("UASInviteTransaction received unexpected response, " + sipResponse.ReasonPhrase + " from " + remoteEndPoint.ToString() + ", ignoring.");
        }

        private void UASInviteTransaction_TransactionRequestReceived(SIPEndPoint localSIPEndPoint, SIPEndPoint remoteEndPoint, SIPTransaction sipTransaction, SIPRequest sipRequest)
        {
            try
            {
                if (TransactionState == SIPTransactionStatesEnum.Terminated)
                {
                    logger.Debug("Request received by UASInviteTransaction for a terminated transaction, ignoring.");
                }
                else if (sipRequest.Method != SIPMethodsEnum.INVITE)
                {
                    logger.Warn("Unexpected " + sipRequest.Method + " passed to UASInviteTransaction.");
                }
                else
                {
                    if (TransactionState != SIPTransactionStatesEnum.Trying)
                    {
                        SIPResponse tryingResponse = GetInfoResponse(m_transactionRequest, SIPResponseStatusCodesEnum.Trying);
                        SendInformationalResponse(tryingResponse);
                    }

                    // Process INVITE.
                    if (UASSIPRequestAuthenticate != null)
                    {
                        m_authorisationResult = UASSIPRequestAuthenticate(localSIPEndPoint, remoteEndPoint, sipRequest);
                        if (m_authorisationResult.Authorised)
                        {
                            if (NewCallReceived != null)
                            {
                                NewCallReceived(localSIPEndPoint, remoteEndPoint, this, sipRequest);
                            }
                            else
                            {
                                // Nobody wants the call so return an error response.
                                SIPResponse declinedResponse = SIPTransport.GetResponse(sipRequest, SIPResponseStatusCodesEnum.Decline, "Nothing listening");
                                SendFinalResponse(declinedResponse);
                            }
                        }
                        else
                        {
                            // Send authorisation failure or required response
                            SIPResponse authReqdResponse = SIPTransport.GetResponse(sipRequest, m_authorisationResult.ErrorResponse, null);
                            authReqdResponse.Header.AuthenticationHeader = m_authorisationResult.AuthenticationRequiredHeader;
                            SendFinalResponse(authReqdResponse);
                        }
                    }
                    else
                    {
                        if (NewCallReceived != null)
                        {
                            NewCallReceived(localSIPEndPoint, remoteEndPoint, this, sipRequest);
                        }
                        else
                        {
                            // Nobody wants the call so return an error response.
                            SIPResponse declinedResponse = SIPTransport.GetResponse(sipRequest, SIPResponseStatusCodesEnum.Decline, "Nothing listening");
                            SendFinalResponse(declinedResponse);
                        }
                    }
                }
            }
            catch (Exception excp)
            {
                logger.Error("Exception UASInviteTransaction GotRequest. " + excp.Message);
            }
        }

        public override void SendInformationalResponse(SIPResponse sipResponse)
        {
            base.SendInformationalResponse(sipResponse);

            if (CDR != null)
            {
                CDR.Progress(sipResponse.Status, sipResponse.ReasonPhrase);
            }
        }

        public override void SendFinalResponse(SIPResponse sipResponse)
        {
            base.SendFinalResponse(sipResponse);

            if (CDR != null)
            {
                CDR.Answered(sipResponse.StatusCode, sipResponse.Status, sipResponse.ReasonPhrase);
            }
        }

        public void CancelCall()
        {
            if (TransactionState == SIPTransactionStatesEnum.Trying || TransactionState == SIPTransactionStatesEnum.Proceeding)
            {
                base.Cancel();

                SIPResponse cancelResponse = SIPTransport.GetResponse(TransactionRequest, SIPResponseStatusCodesEnum.RequestTerminated, null);
                SendFinalResponse(cancelResponse);

                if (UASInviteTransactionCancelled != null)
                {
                    UASInviteTransactionCancelled(this);
                }
            }
            else
            {
                logger.Warn("A request was made to cancel transaction " + TransactionId + " that was not in the trying or proceeding states, state=" + TransactionState + ".");
            }

            if (CDR != null)
            {
                CDR.Cancelled();
            }
        }

        public SIPResponse GetAuthReqdResponse(SIPRequest sipRequest, SIPEndPoint localSIPEndPoint, string realm, string nonce)
        {
            try
            {
                SIPResponse authReqdResponse = new SIPResponse(SIPResponseStatusCodesEnum.ProxyAuthenticationRequired, null, sipRequest.LocalSIPEndPoint);
                SIPAuthenticationHeader authHeader = new SIPAuthenticationHeader(SIPAuthorisationHeadersEnum.ProxyAuthenticate, realm, nonce);

                SIPHeader requestHeader = sipRequest.Header;
                SIPHeader unauthHeader = new SIPHeader(new SIPContactHeader(null, new SIPURI(sipRequest.URI.Scheme, localSIPEndPoint)), requestHeader.From, requestHeader.To, requestHeader.CSeq, requestHeader.CallId);
                unauthHeader.CSeqMethod = requestHeader.CSeqMethod;
                unauthHeader.Vias = requestHeader.Vias;
                unauthHeader.AuthenticationHeader = authHeader;
                unauthHeader.UserAgent = m_sipServerAgent;
                unauthHeader.MaxForwards = Int32.MinValue;

                authReqdResponse.Header = unauthHeader;

                return authReqdResponse;
            }
            catch (Exception excp)
            {
                logger.Error("Exception GetAuthReqdResponse. " + excp.Message);
                throw excp;
            }
        }

        public SIPResponse GetOkResponse(SIPRequest sipRequest, SIPEndPoint localSIPEndPoint, string contentType, string messageBody)
        {
            try
            {
                SIPResponse okResponse = new SIPResponse(SIPResponseStatusCodesEnum.Ok, null, sipRequest.LocalSIPEndPoint);

                SIPHeader requestHeader = sipRequest.Header;
                okResponse.Header = new SIPHeader(new SIPContactHeader(null, new SIPURI(sipRequest.URI.Scheme, localSIPEndPoint)), requestHeader.From, requestHeader.To, requestHeader.CSeq, requestHeader.CallId);
                okResponse.Header.To.ToTag = m_localTag;
                okResponse.Header.CSeqMethod = requestHeader.CSeqMethod;
                okResponse.Header.Vias = requestHeader.Vias;
                okResponse.Header.Server = m_sipServerAgent;
                okResponse.Header.MaxForwards = Int32.MinValue;
                okResponse.Header.RecordRoutes = requestHeader.RecordRoutes;
                okResponse.Body = messageBody;
                okResponse.Header.ContentType = contentType;
                okResponse.Header.ContentLength = (messageBody != null) ? messageBody.Length : 0;

                return okResponse;
            }
            catch (Exception excp)
            {
                logger.Error("Exception GetOkResponse. " + excp.Message);
                throw excp;
            }
        }
	}
}