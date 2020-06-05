using Dell.Services.SupportAssist.CorelibInfrastructure;
using Dell.Services.SupportAssist.Logger;
using Dell.Services.SupportAssist.SecurityUtility;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Dell.Services.SupportAssist.CommunicationPipe
{
    public class CommunicationPipeClient : ICommunicationPipeClient
    {
        private static readonly ILogger Logger = LogManager.GetLogger(typeof(CommunicationPipeClient));

        private NamedPipeClientStream _clientPipeStream;
        private string randomValidationValue = string.Empty;
        //private const string ASYMMETRICKEY = "MTg1MSE8UlNBS2V5VmFsdWU+PE1vZHVsdXM+cE9QaXM1RjJ2TTdRY1VhNzJSRE5ibThSTGNSazBrQXRuc0ZiR0s2NG1PZTdTbnlxVFpwMzhmSkJJRWZVM3Y5WFU1ZjZDR1BSYmRWRlR2bFdKa1pHOFE9PTwvTW9kdWx1cz48RXhwb25lbnQ+QVFBQjwvRXhwb25lbnQ+PC9SU0FLZXlWYWx1ZT4=";
        private const string ASYMMETRICKEY = "MTE4NyE8UlNBS2V5VmFsdWU+PE1vZHVsdXM+c2oxeVJZbXdzNEhBNGNGZkwweVFSaGRmcFNFSlBmaEp0SmZ2QVFpOTVsQXQwUjFIN3ZST0lBMzQ2SzY3YnNUWm14THhjM28vWTQrSEhtTG1CUDd5K0dDMUdBdlMzMS9mQkIxYXhnbk9GUVptZlVUTlJHaVlrKzNkbnBCaSs2YXI5L29STXdvelNESjRVanhpYlp5ci9tTVM4TEdjNmpQZllnN0J4YURodS8wPTwvTW9kdWx1cz48RXhwb25lbnQ+QVFBQjwvRXhwb25lbnQ+PC9SU0FLZXlWYWx1ZT4=";
        private const byte PIPE_CONN_TIMEOUT_IN_MINS = 1;
        private ManualResetEvent NamePipeClientManualResetEvent { get; set; }
        private bool IsFirstServiceRequest { get; set; } = true;
        private bool IsServerValidated { get; set; } = false;


        public event EventHandler<MessageEventArguments> OnMessageReceived;
        public event EventHandler<EventArgs> OnPipeClosed;
        public string PipeName { get; private set; }

        public ICommunicationPipeSignatureVerifier SignatureVerifier { get; set; }

        public bool IsConnected
        {
            get
            {
                return _clientPipeStream == null ? false : _clientPipeStream.IsConnected;
            }
        }

        public CommunicationPipeClient(string pipeName)
        {
            SignatureVerifier = new CommunicationPipeSignatureVerifier();

            PipeName = pipeName;
            _clientPipeStream = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        }

        public Task SendMessage(string message)
        {
            Task sendMessageTeask = null;
            try
            {
                sendMessageTeask = WriteBytes(Encoding.UTF8.GetBytes(message));
            }
            catch (Exception ex)
            {
                Logger.Error($"Error occured while sending message to server: {ex.Message}");
            }

            return sendMessageTeask;
        }
        /// <summary>
        /// Connect to namepipe server provided in constructor parameter
        /// </summary>
        public void Connect()
        {
            System.Diagnostics.Debugger.Launch();
            System.Diagnostics.Debugger.Break();
            EReturnCode isValidSign;
            try
            {
                if (_clientPipeStream.IsConnected)
                    return;
                //initiate connection to server
                int timeOutInMS = (int)new TimeSpan(0, PIPE_CONN_TIMEOUT_IN_MINS, 0).TotalMilliseconds;
                Logger.Debug("Pipe client connection initialization started");
                _clientPipeStream.Connect(timeOutInMS);
                Logger.Debug("Pipe client connection initialization finished");
                isValidSign = SignatureVerifier.IsNamePipeServerIsSignedByDell(_clientPipeStream);

                //after connection start reading the byte stream
                StartStringReaderAsync();

                //3-Feb-2020
                //Check the server process certificae
                switch (isValidSign)
                {
                    case EReturnCode.AccessDenied:
                        //Condition when signature check failed because of access rights.
                        //publicKey = ConfigurationManager.AppSettings["PublicKey"].ToString();
                        randomValidationValue = RandomString(); 
                        //create random string value and  encrypt it and send to client
                        string ecryptedValue = AsymmetricEncryptionDecrpytion.EncryptDataInString(ASYMMETRICKEY, randomValidationValue); 
                        ClientRequest clientRequest = new ClientRequest { RequestType = "ValidateServerSignature" };
                        List<Arguments> argsList = new List<Arguments>();
                        argsList.Add(new Arguments() { Key = "ValidateServerSignature", Value = ecryptedValue });
                        clientRequest.RequestArguments = argsList;
                        SendMessage(new JavaScriptSerializer().Serialize(clientRequest));
                        NamePipeClientManualResetEvent = new ManualResetEvent(false);
                        NamePipeClientManualResetEvent.WaitOne(30000);
                        if (!IsServerValidated)
                        {
                            //send message to server to validate server
                            Disconnect();
                            Logger.Error("Namepipe server certificate is not valid");
                            return;
                        }
                        break;
                    case EReturnCode.Unknown:
                    case EReturnCode.SignFailed:
                        Disconnect();
                        Logger.Error("Namepipe server certificate is not valid");
                        return;
                    case EReturnCode.Success:
                        break;
                };
                IsServerValidated = true;
            }
            catch (Exception ex)
            {
                Logger.Error($"Namepipe client connection failed: {ex.Message}");
                Disconnect();
                return;
            }

            Logger.Debug("Namepipe client connection successful");
        }

        private string RandomString()
        {
            StringBuilder builder = new StringBuilder();
            Random random = new Random();
            char ch;
            for (int i = 0; i < 5; i++)
            {
                ch = Convert.ToChar(Convert.ToInt32(Math.Floor(26 * random.NextDouble() + 65)));
                builder.Append(ch);
            }
            return builder.ToString().ToLower();
        }

        public void Disconnect()
        {
            try
            {
                if (_clientPipeStream != null)
                {
                    //Wait for pipe to drain out is not required,
                    //as we do not care about server to read data after stoping the client
                    _clientPipeStream.Close();
                    _clientPipeStream.Dispose();
                    _clientPipeStream = null;
                    Logger.Info("Namepipe client connection closed sucessfully.");
                }

                //Dispose the AutoReset Event
                DisposeNamePipeClientManualResetEvent();
                IsFirstServiceRequest = true;
                IsServerValidated = false;
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to close namepipe client connection: {ex.Message}");
                return;
            }
        }
        private void DisposeNamePipeClientManualResetEvent()
        {
            if (NamePipeClientManualResetEvent != null && !NamePipeClientManualResetEvent.SafeWaitHandle.IsInvalid)
            {
                NamePipeClientManualResetEvent.Close();
                NamePipeClientManualResetEvent.Dispose();
                NamePipeClientManualResetEvent = null;
            }
        }
        private Task WriteBytes(byte[] bytes)
        {
            Task writeTask = null;
            var byteLength = BitConverter.GetBytes(bytes.Length);
            var byteArray = byteLength.Concat(bytes).ToArray();

            if (_clientPipeStream.CanWrite)
                writeTask = _clientPipeStream.WriteAsync(byteArray, 0, byteArray.Length);

            return writeTask;
        }

        private void StartStringReaderAsync()
        {
            //Start reading byte stream aysnchroneously once client connected
            StartByteReaderAsync((byteString) =>
            {
                string stringMsg = Encoding.UTF8.GetString(byteString).TrimEnd('\0');

                if (IsFirstServiceRequest && !IsServerValidated)
                {
                    IsFirstServiceRequest = false;
                    //bool isSignVerified = AsymmetricEncryptionDecrpytion.VerifyData(randomValidationValue, publicKey, byteString);
                    bool isSignVerified = AsymmetricEncryptionDecrpytion.VerifyData(randomValidationValue, ASYMMETRICKEY, byteString);

                    if (isSignVerified)
                    {
                        Logger.Info("Fallback mechanism passed");
                        IsServerValidated = true;
                        NamePipeClientManualResetEvent.Set();
                        return;
                    }
                }
                OnMessageReceived?.Invoke(this, new MessageEventArguments(stringMsg));
            });
        }

        private void StartByteReaderAsync(Action<byte[]> packetReceived)
        {
            int packetSize = sizeof(int);
            byte[] packetBytesLength = new byte[packetSize];

            if (_clientPipeStream != null && !_clientPipeStream.SafePipeHandle.IsClosed)
            {
                _clientPipeStream?.ReadAsync(packetBytesLength, 0, packetSize).ContinueWith(readerTask =>
                {
                    int len = readerTask.Result;

                    if (len == 0)
                    {
                        Logger.Debug("Namepipe client disconnected from server");
                        OnPipeClosed?.Invoke(this, EventArgs.Empty);

                        //Close the connection so that new client will be sucess next time
                        Disconnect();
                        return;
                    }

                    int dataLength = BitConverter.ToInt32(packetBytesLength, 0);
                    byte[] data = new byte[dataLength];
                    if (_clientPipeStream != null && !_clientPipeStream.SafePipeHandle.IsClosed)
                    {
                        _clientPipeStream?.ReadAsync(data, 0, dataLength).ContinueWith(clientReaderTask =>
                        {
                            try
                            {
                                len = clientReaderTask.Result;

                                if (len == 0)
                                {
                                    Logger.Debug("Namepipe client disconnected from server");
                                    OnPipeClosed?.Invoke(this, EventArgs.Empty);

                                    //Close the connection so that new client will be sucess next time
                                    Disconnect();
                                }
                                else
                                {
                                    //Start async reader again
                                    packetReceived(data);
                                    if (_clientPipeStream != null && !_clientPipeStream.SafePipeHandle.IsClosed)
                                        StartByteReaderAsync(packetReceived);
                                }
                            }
                            catch (System.ObjectDisposedException ex)
                            {
                                //This will happen when the object has been disposed
                                Logger.Error($"Exception occured while reading server byte stream:{ex.Message}");
                            }
                        });
                    }
                });
            }
        }
    }

    public sealed class ClientRequest
    {
        public string RequestType { get; set; }
        public List<Arguments> RequestArguments { get; set; }
        public List<Arguments> Response { get; set; }
    }

    public sealed class Arguments
    {
        public string Key { get; set; }
        public string Value { get; set; }
    }
}
