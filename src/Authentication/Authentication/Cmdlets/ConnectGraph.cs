﻿// ------------------------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All Rights Reserved.  Licensed under the MIT License.  See License in the project root for license information.
// ------------------------------------------------------------------------------
namespace Microsoft.Graph.PowerShell.Authentication.Cmdlets
{
    using Microsoft.Graph.Auth;
    using Microsoft.Graph.PowerShell.Authentication.Helpers;
    using Microsoft.Graph.PowerShell.Authentication.Models;
    using Microsoft.Identity.Client;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Management.Automation;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Net;
    using System.Globalization;

    [Cmdlet(VerbsCommunications.Connect, "Graph", DefaultParameterSetName = Constants.UserParameterSet)]
    public class ConnectGraph : PSCmdlet, IModuleAssemblyInitializer, IModuleAssemblyCleanup
    {
        [Parameter(ParameterSetName = Constants.UserParameterSet,
            Position = 1,
            HelpMessage = "An array of delegated permissions to consent to.")]
        public string[] Scopes { get; set; }

        [Parameter(ParameterSetName = Constants.AppParameterSet,
            Position = 1,
            Mandatory = true,
            HelpMessage = "The client id of your application.")]
        public string ClientId { get; set; }

        [Parameter(ParameterSetName = Constants.AppParameterSet,
            Position = 2,
            HelpMessage = "The name of your certificate. The Certificate will be retrieved from the current user's certificate store.")]
        public string CertificateName { get; set; }

        [Parameter(ParameterSetName = Constants.AppParameterSet,
            Position = 3,
            HelpMessage = "The thumbprint of your certificate. The Certificate will be retrieved from the current user's certificate store.")]
        public string CertificateThumbprint { get; set; }
        
        [Parameter(ParameterSetName = Constants.AccessTokenParameterSet,
            Position = 1,
            HelpMessage = "Specifies a bearer token for Microsoft Graph service. Access tokens do timeout and you'll have to handle their refresh.")]
        public string AccessToken { get; set; }

        [Parameter(Position = 4,
            HelpMessage = "The id of the tenant to connect to.")]
        public string TenantId { get; set; }

        [Parameter(Position = 5,
            HelpMessage = "Forces the command to get a new access token silently.")]
        public SwitchParameter ForceRefresh { get; set; }

        [Parameter(Mandatory = false,
            HelpMessage = "Determines the scope of authentication context. This accepts `Process` for the current process, or `CurrentUser` for all sessions started by user.")]
        public ContextScope ContextScope { get; set; }

        private CancellationTokenSource cancellationTokenSource;

        protected override void BeginProcessing()
        {
            ValidateParameters();
            base.BeginProcessing();
        }

        protected override void EndProcessing()
        {
            base.EndProcessing();
        }

        protected override void ProcessRecord()
        {
            base.ProcessRecord();
            IAuthContext authContext = new AuthContext { TenantId = TenantId };
            cancellationTokenSource = new CancellationTokenSource();

            switch (ParameterSetName)
            {
                case Constants.UserParameterSet:
                    {
                        // 2 mins timeout. 1 min < HTTP timeout.
                        TimeSpan authTimeout = new TimeSpan(0, 0, Constants.MaxDeviceCodeTimeOut);
                        cancellationTokenSource = new CancellationTokenSource(authTimeout);
                        authContext.AuthType = AuthenticationType.Delegated;
                        authContext.Scopes = Scopes ?? new string[] { "User.Read" };
                        // Default to CurrentUser but allow the customer to change this via `ContextScope` param.
                        authContext.ContextScope = this.IsParameterBound(nameof(ContextScope)) ? ContextScope : ContextScope.CurrentUser;
                    }
                    break;
                case Constants.AppParameterSet:
                    {
                        authContext.AuthType = AuthenticationType.AppOnly;
                        authContext.ClientId = ClientId;
                        authContext.CertificateThumbprint = CertificateThumbprint;
                        authContext.CertificateName = CertificateName;
                        // Default to Process but allow the customer to change this via `ContextScope` param.
                        authContext.ContextScope = this.IsParameterBound(nameof(ContextScope)) ? ContextScope : ContextScope.Process;
                    }
                    break;
                case Constants.AccessTokenParameterSet:
                    {
                        authContext.AuthType = AuthenticationType.UserProvidedAccessToken;
                        authContext.ContextScope = ContextScope.Process;
                        // Store user provided access token to a session object.
                        GraphSession.Instance.UserProvidedToken = new NetworkCredential(string.Empty, AccessToken).SecurePassword;
                    }
                    break;
            }

            CancellationToken cancellationToken = cancellationTokenSource.Token;

            try
            {
                // Gets a static instance of IAuthenticationProvider when the client app hasn't changed. 
                IAuthenticationProvider authProvider = AuthenticationHelpers.GetAuthProvider(authContext);
                IClientApplicationBase clientApplication = null;
                if (ParameterSetName == Constants.UserParameterSet)
                {
                    clientApplication = (authProvider as DeviceCodeProvider).ClientApplication;
                }
                else if (ParameterSetName == Constants.AppParameterSet)
                {
                    clientApplication = (authProvider as ClientCredentialProvider).ClientApplication;
                }

                // Incremental scope consent without re-instantiating the auth provider. We will use a static instance.
                GraphRequestContext graphRequestContext = new GraphRequestContext();
                graphRequestContext.CancellationToken = cancellationToken;
                graphRequestContext.MiddlewareOptions = new Dictionary<string, IMiddlewareOption>
                {
                    {
                        typeof(AuthenticationHandlerOption).ToString(),
                        new AuthenticationHandlerOption
                        {
                            AuthenticationProviderOption = new AuthenticationProviderOption
                            {
                                Scopes = authContext.Scopes,
                                ForceRefresh = ForceRefresh
                            }
                        }
                    }
                };

                // Trigger consent.
                HttpRequestMessage httpRequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0/me");
                httpRequestMessage.Properties.Add(typeof(GraphRequestContext).ToString(), graphRequestContext);
                authProvider.AuthenticateRequestAsync(httpRequestMessage).GetAwaiter().GetResult();

                IAccount account = null;
                if (clientApplication != null)
                {
                    // Only get accounts when we are using MSAL to get an access token.
                    IEnumerable<IAccount> accounts = clientApplication.GetAccountsAsync().GetAwaiter().GetResult();
                    account = accounts.FirstOrDefault();
                }
                DecodeJWT(httpRequestMessage.Headers.Authorization?.Parameter, account, ref authContext);

                // Save auth context to session state.
                GraphSession.Instance.AuthContext = authContext;
            }
            catch (AuthenticationException authEx)
            {
                if ((authEx.InnerException is TaskCanceledException) && cancellationToken.IsCancellationRequested)
                {
                    // DeviceCodeTimeout
                    throw new Exception(string.Format(
                            CultureInfo.CurrentCulture,
                            ErrorConstants.Message.DeviceCodeTimeout,
                            Constants.MaxDeviceCodeTimeOut));
                }
                else
                {
                    throw authEx.InnerException ?? authEx;
                }
            }
            catch (Exception ex)
            {
                throw ex.InnerException ?? ex;
            }

            WriteObject("Welcome To Microsoft Graph!");
        }

        protected override void StopProcessing()
        {
            cancellationTokenSource.Cancel();
            base.StopProcessing();
        }

        private void ValidateParameters()
        {
            switch (ParameterSetName)
            {
                case Constants.AppParameterSet:
                    {
                        // Client Id
                        if (string.IsNullOrEmpty(ClientId))
                        {
                            ThrowParameterError(nameof(ClientId));
                        }

                        // Certificate Thumbprint or name
                        if (string.IsNullOrEmpty(CertificateThumbprint) && string.IsNullOrEmpty(CertificateName))
                        {
                            ThrowParameterError($"{nameof(CertificateThumbprint)} or {nameof(CertificateName)}");
                        }

                        // Tenant Id
                        if (string.IsNullOrEmpty(TenantId))
                        {
                            ThrowParameterError(nameof(TenantId));
                        }

                    }
                    break;
                case Constants.AccessTokenParameterSet:
                    {
                        // AccessToken
                        if (string.IsNullOrEmpty(AccessToken))
                        {
                            ThrowParameterError(nameof(AccessToken));
                        }
                    }
                    break;
            }
        }

        private void ThrowParameterError(string parameterName)
        {
            ThrowTerminatingError(
                new ErrorRecord(
                    new ArgumentException($"Must specify {parameterName}"), Guid.NewGuid().ToString(), ErrorCategory.InvalidArgument, null)
                );
        }

        private void DecodeJWT(string token, IAccount account, ref IAuthContext authContext)
        {
            JwtPayload jwtPayload = JwtHelpers.DecodeToObject<JwtPayload>(token);
            if (jwtPayload == null && authContext.AuthType == AuthenticationType.UserProvidedAccessToken)
            {
                throw new Exception(string.Format(
                        CultureInfo.CurrentCulture,
                        ErrorConstants.Message.InvalidUserProvidedToken,
                        nameof(AccessToken)));
            }

            authContext.ClientId = jwtPayload?.Appid ?? authContext.ClientId;
            authContext.Scopes = jwtPayload?.Scp?.Split(' ') ?? jwtPayload?.Roles;
            authContext.TenantId = jwtPayload?.Tid ?? account?.HomeAccountId?.TenantId;
            authContext.AppName = jwtPayload?.AppDisplayname;
            authContext.Account = jwtPayload?.Upn ?? account?.Username;
        }

        /// <summary>
        /// Globally initializes GraphSession.
        /// </summary>
        public void OnImport()
        {
            GraphSessionInitializer.InitializeSession();
        }

        /// <summary>
        /// Resets <see cref="GraphSession"/> instance when a user removes the module from the session via Remove-Module.
        /// </summary>
        /// <param name="psModuleInfo">A <see cref="PSModuleInfo"/> object.</param>
        public void OnRemove(PSModuleInfo psModuleInfo)
        {
            GraphSession.Reset();
        }
    }
}
