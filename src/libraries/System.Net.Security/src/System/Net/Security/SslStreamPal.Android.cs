// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Security.Authentication;
using System.Security.Authentication.ExtendedProtection;
using System.Security.Cryptography.X509Certificates;

using PAL_SSLStreamStatus = Interop.AndroidCrypto.PAL_SSLStreamStatus;

namespace System.Net.Security
{
    internal static class SslStreamPal
    {
        public static Exception GetException(SecurityStatusPal status)
        {
            return status.Exception ?? new Interop.AndroidCrypto.SslException((int)status.ErrorCode);
        }

        internal const bool StartMutualAuthAsAnonymous = false;
        internal const bool CanEncryptEmptyMessage = false;

        public static void VerifyPackageInfo()
        {
        }

        public static SecurityStatusPal AcceptSecurityContext(
            ref SafeFreeCredentials credential,
            ref SafeDeleteSslContext? context,
            ReadOnlySpan<byte> inputBuffer,
            ref byte[]? outputBuffer,
            SslAuthenticationOptions sslAuthenticationOptions)
        {
            return HandshakeInternal(credential, ref context, inputBuffer, ref outputBuffer, sslAuthenticationOptions);
        }

        public static SecurityStatusPal InitializeSecurityContext(
            ref SafeFreeCredentials credential,
            ref SafeDeleteSslContext? context,
            string? targetName,
            ReadOnlySpan<byte> inputBuffer,
            ref byte[]? outputBuffer,
            SslAuthenticationOptions sslAuthenticationOptions)
        {
            return HandshakeInternal(credential, ref context, inputBuffer, ref outputBuffer, sslAuthenticationOptions);
        }

        public static SafeFreeCredentials AcquireCredentialsHandle(
            SslStreamCertificateContext? certificateContext,
            SslProtocols protocols,
            EncryptionPolicy policy,
            bool isServer)
        {
            return new SafeFreeSslCredentials(certificateContext, protocols, policy);
        }

        internal static byte[]? GetNegotiatedApplicationProtocol(SafeDeleteContext? context)
        {
            if (context == null)
                return null;

            return Interop.AndroidCrypto.SSLStreamGetApplicationProtocol(((SafeDeleteSslContext)context).SslContext);
        }

        public static SecurityStatusPal EncryptMessage(
            SafeDeleteContext securityContext,
            ReadOnlyMemory<byte> input,
            int headerSize,
            int trailerSize,
            ref byte[] output,
            out int resultSize)
        {
            resultSize = 0;
            Debug.Assert(input.Length > 0, $"{nameof(input.Length)} > 0 since {nameof(CanEncryptEmptyMessage)} is false");

            try
            {
                SafeDeleteSslContext sslContext = (SafeDeleteSslContext)securityContext;
                SafeSslHandle sslHandle = sslContext.SslContext;

                PAL_SSLStreamStatus ret = Interop.AndroidCrypto.SSLStreamWrite(sslHandle, input);
                SecurityStatusPalErrorCode statusCode = ret switch
                {
                    PAL_SSLStreamStatus.OK => SecurityStatusPalErrorCode.OK,
                    PAL_SSLStreamStatus.NeedData => SecurityStatusPalErrorCode.ContinueNeeded,
                    PAL_SSLStreamStatus.Renegotiate => SecurityStatusPalErrorCode.Renegotiate,
                    PAL_SSLStreamStatus.Closed => SecurityStatusPalErrorCode.ContextExpired,
                    _ => SecurityStatusPalErrorCode.InternalError
                };

                if (sslContext.BytesReadyForConnection <= output?.Length)
                {
                    resultSize = sslContext.ReadPendingWrites(output, 0, output.Length);
                }
                else
                {
                    output = sslContext.ReadPendingWrites()!;
                    resultSize = output.Length;
                }

                return new SecurityStatusPal(statusCode);
            }
            catch (Exception e)
            {
                return new SecurityStatusPal(SecurityStatusPalErrorCode.InternalError, e);
            }
        }

        public static SecurityStatusPal DecryptMessage(
            SafeDeleteContext securityContext,
            byte[] buffer,
            ref int offset,
            ref int count)
        {
            try
            {
                SafeDeleteSslContext sslContext = (SafeDeleteSslContext)securityContext;
                SafeSslHandle sslHandle = sslContext.SslContext;

                sslContext.Write(buffer.AsSpan(offset, count));

                PAL_SSLStreamStatus ret = Interop.AndroidCrypto.SSLStreamRead(sslHandle, buffer.AsSpan(offset, count), out int read);
                if (ret == PAL_SSLStreamStatus.Error)
                    return new SecurityStatusPal(SecurityStatusPalErrorCode.InternalError);

                count = read;

                SecurityStatusPalErrorCode statusCode = ret switch
                {
                    PAL_SSLStreamStatus.OK => SecurityStatusPalErrorCode.OK,
                    PAL_SSLStreamStatus.NeedData => SecurityStatusPalErrorCode.OK,
                    PAL_SSLStreamStatus.Renegotiate => SecurityStatusPalErrorCode.Renegotiate,
                    PAL_SSLStreamStatus.Closed => SecurityStatusPalErrorCode.ContextExpired,
                    _ => SecurityStatusPalErrorCode.InternalError
                };

                return new SecurityStatusPal(statusCode);
            }
            catch (Exception e)
            {
                return new SecurityStatusPal(SecurityStatusPalErrorCode.InternalError, e);
            }
        }

        public static ChannelBinding? QueryContextChannelBinding(
            SafeDeleteContext securityContext,
            ChannelBindingKind attribute)
        {
            if (attribute == ChannelBindingKind.Endpoint)
                return EndpointChannelBindingToken.Build(securityContext);

            throw new NotImplementedException(nameof(QueryContextChannelBinding));
        }

        public static void QueryContextStreamSizes(
            SafeDeleteContext? securityContext,
            out StreamSizes streamSizes)
        {
            streamSizes = StreamSizes.Default;
        }

        public static void QueryContextConnectionInfo(
            SafeDeleteContext securityContext,
            out SslConnectionInfo connectionInfo)
        {
            connectionInfo = new SslConnectionInfo(((SafeDeleteSslContext)securityContext).SslContext);
        }

        private static SecurityStatusPal HandshakeInternal(
            SafeFreeCredentials credential,
            ref SafeDeleteSslContext? context,
            ReadOnlySpan<byte> inputBuffer,
            ref byte[]? outputBuffer,
            SslAuthenticationOptions sslAuthenticationOptions)
        {
            Debug.Assert(!credential.IsInvalid);

            try
            {
                SafeDeleteSslContext? sslContext = ((SafeDeleteSslContext?)context);

                if ((context == null) || context.IsInvalid)
                {
                    context = new SafeDeleteSslContext((credential as SafeFreeSslCredentials)!, sslAuthenticationOptions);
                    sslContext = context;
                }

                if (inputBuffer.Length > 0)
                {
                    sslContext!.Write(inputBuffer);
                }

                SafeSslHandle sslHandle = sslContext!.SslContext;

                PAL_SSLStreamStatus ret = Interop.AndroidCrypto.SSLStreamHandshake(sslHandle);
                SecurityStatusPalErrorCode statusCode = ret switch
                {
                    PAL_SSLStreamStatus.OK => SecurityStatusPalErrorCode.OK,
                    PAL_SSLStreamStatus.NeedData => SecurityStatusPalErrorCode.ContinueNeeded,
                    _ => SecurityStatusPalErrorCode.InternalError
                };

                outputBuffer = sslContext.ReadPendingWrites();

                return new SecurityStatusPal(statusCode);
            }
            catch (Exception exc)
            {
                return new SecurityStatusPal(SecurityStatusPalErrorCode.InternalError, exc);
            }
        }

        public static SecurityStatusPal ApplyAlertToken(
            ref SafeFreeCredentials? credentialsHandle,
            SafeDeleteContext? securityContext,
            TlsAlertType alertType,
            TlsAlertMessage alertMessage)
        {
            // There doesn't seem to be an exposed API for writing an alert.
            // The API seems to assume that all alerts are generated internally.
            return new SecurityStatusPal(SecurityStatusPalErrorCode.OK);
        }

        public static SecurityStatusPal ApplyShutdownToken(
            ref SafeFreeCredentials? credentialsHandle,
            SafeDeleteContext securityContext)
        {
            SafeDeleteSslContext sslContext = ((SafeDeleteSslContext)securityContext);
            SafeSslHandle sslHandle = sslContext.SslContext;


            bool success = Interop.AndroidCrypto.SSLStreamShutdown(sslHandle);
            if (success)
            {
                return new SecurityStatusPal(SecurityStatusPalErrorCode.OK);
            }

            return new SecurityStatusPal(SecurityStatusPalErrorCode.InternalError);
        }
    }
}