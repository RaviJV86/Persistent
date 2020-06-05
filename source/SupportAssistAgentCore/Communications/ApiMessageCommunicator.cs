using Dell.Services.SupportAssist.ApplicationSettings;
using Dell.Services.SupportAssist.CommunicationPipe;
using Dell.Services.SupportAssist.CorelibInfrastructure;
using Dell.Services.SupportAssist.DataAccessLayer;
using Dell.Services.SupportAssist.DomainModel;
using Dell.Services.SupportAssist.Logger;
using Dell.Services.SupportAssist.Persistence.Helpers;
using Dell.Services.SupportAssist.SecurityUtility;
using Dell.Services.SupportAssist.SupportAssistInfrastructure.RemoteCommandExecution;
using Dell.Services.SupportAssist.SupportAssistUtilities.Utilities;
using Dell.Services.SupportAssist.UtilityFiles;
using Dell.Services.SupportAssist.WorkflowEngine;
using Dell.Services.SupportAssist.WorkflowEngine.WorkFlowSession;
using Microsoft.Practices.Unity;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Web.Script.Serialization;
using System.Threading;
using Dell.Services.SupportAssist.SupportAssistWebServer;

namespace Dell.Services.SupportAssist.SupportAssistAgentCore.Communications
{
    public class ApiMessageCommunicator : IApiMessageCommunicator
    {
        private static readonly ILogger Logger = LogManager.GetLogger(typeof(ApiMessageCommunicator));
        private string successString = "Success";
        private readonly string restartReason = "UIRequest";

        [Dependency]
        public IWorkflowSessionManager WorkflowSessionManager { get; set; }

        [Dependency]
        public IWorkflowEngineManager WorkflowEngineManager { get; set; }

        [Dependency]
        public ISupportAssistProcessor SupportAssistProcessor { get; set; }

        [Dependency]
        public ISupportAssistCommercialProcessor SupportAssistCommercialProcessor { get; set; }

        [Dependency]
        public ISupportAssistAlertProcessor SupportAssistAlertProcessor { get; set; }

        [Dependency]
        public IDataAccess DataAccess { get; set; }

        [Dependency]
        public IPrivacySettingsCheck PrivacySettingsCheck { get; set; }

        [Dependency]
        public IScheduledWorkflowTask ScheduledWorkflowTask { get; set; }
        [Dependency]
        public ISupportAssistRemoteCommandProcessor SupportAssistRemoteCommandProcessor { get; set; }

        [Dependency]
        public IAppxHelper AppxHelper { get; set; }

        [Dependency]
        public IWebSocketServerManager WebSocketServerManager { get; set; }

        //Request Error Constant
        public const string REQUESTERROR = "Error: invalid Request";

        public ApiMessageCommunicator()
        {

        }
        /// <summary>
        /// Method to process the Namepipe client requrest and send the response
        /// NOTE: it must be return some values to the Client. otherwise unncessarey message delay will happend
        /// Also it may misbehave.
        /// </summary>
        /// <param name="pipeServer">PipeServer object to response message to client.</param>
        /// <param name="requestMessage">request message from client.</param>
        /// <returns>return processing status true/false.</returns>
        public bool ExecuteRequest(ICommunicationPipeServer pipeServer, string requestMessage)
        {            
            ClientRequest clientRequest = new ClientRequest();
            try
            {
                if (!string.IsNullOrEmpty(requestMessage))
                    clientRequest = JsonConvert.DeserializeObject<ClientRequest>(requestMessage);
                //TODO: logic to be finalized in case if the command does not returns any value
                //Add the switch case here to be handle                
                switch (clientRequest.RequestType)
                {
                    case "ValidateServerSignature":
                        pipeServer.SendByteMessage(ValidateServerSignature(clientRequest.RequestArguments));
                        break;
                    case "GetLaunchArguments":
                        //Check if websocket server restart required
                        CheckandRestartWSServer(clientRequest.RequestArguments);
                        //First get the ClientSource
                        ClientSource requiredSource = GetClientSurce(clientRequest);
                        //check if appx version is different from service appx version
                        //In case of WPF this paramter is ignored in UI app xaml code
                        bool versionMismatch = VerifyAppxVersionWithService(clientRequest.RequestArguments);
                        //First save the UWPProtNumber if call from UWP App
                        SaveUWPProxyServerPortNumber(clientRequest);
                        pipeServer.SendMessage(GetLaunchArguments(requiredSource,versionMismatch));
                        break;
                    case "DeRegisterWithPhomeServer":
                        pipeServer.SendMessage(DeRegisterWithPhomeServer());
                        break;
                    case "SaveProxy":
                        pipeServer.SendMessage(SaveProxy(clientRequest.RequestArguments));
                        break;
                    case "GetInstallerInitialSettings":
                        pipeServer.SendMessage(GetInstallerInitialSettings());
                        break;
                    case "IsUIRunningAutoupdateCheck":
                        pipeServer.SendMessage(SupportAssistProcessor.CheckIfUIIsRunning().ToString());
                        break;
                    case "MSTDeployement":
                        {
                            if (clientRequest.RequestArguments.Count > 0)
                            {
                                DeploymentEncodedContent content = new DeploymentEncodedContent() { EncodedData = clientRequest.RequestArguments[0].Value };
                                pipeServer.SendMessage(SupportAssistCommercialProcessor.SetMstConfiguration(content).ToString());
                            }
                        }
                        break;
                    default:
                        //IF no request matches then response with "Error: invalid Request"
                        pipeServer.SendMessage(REQUESTERROR);
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Exception occured while executing namepipe command: {ex.Message}");
                return false;
            }

            return true;
        }

        private void CheckandRestartWSServer(List<Arguments> requestArguments)
        {
            if (requestArguments != null)
            {
                Arguments args = requestArguments.Find(x => x.Key == "WSSRestart");
                if (args != null)
                {
                    bool isRestartRequired;
                    Boolean.TryParse(args.Value, out isRestartRequired);
                    if (!isRestartRequired)
                        return;
                    Logger.Info("WebSocket Server restart initiated.");
                    WebSocketServerManager.Restart(restartReason);
                }
            }
        }


        #region Privaet methods       
        private void SaveUWPProxyServerPortNumber(ClientRequest clientRequest)
        {
            if (clientRequest != null && clientRequest.RequestArguments != null)
            {
                Arguments uwpPortArgs = clientRequest.RequestArguments.Find(x => x.Key == "UWPPortNumber");
                if (uwpPortArgs != null)
                    DataAccess.SaveAppSettingValue(EAppSetting.ProxyServerPortNumber, uwpPortArgs.Value, false);
            }
        }

        private bool VerifyAppxVersionWithService(List<Arguments> arguments)
        {
            bool versionMismatch = false; // version comparision value true by default
            try
            {
                if (arguments != null)
                {
                    Arguments currentAppxVersion = arguments.Find(x => x.Key == "CurrentAppxVersion");
                    if (currentAppxVersion != null)
                    {
                        var serviceVersion = new Version(ApplicationConstant.AppxVersion);
                        var uiAppxVersion = new Version(currentAppxVersion.Value);

                        int isVersionSame = serviceVersion.CompareTo(uiAppxVersion);
                        if (isVersionSame > 0) //Service AppxVersion is Higher
                        {
                            versionMismatch = true;
                            Logger.Info("Appx Version of service is higher");
                            var th = new Thread(() => AppxHelper.InstallAppxPackage(true));
                            th.Start();
                        }
                        else if (isVersionSame < 0) //Service AppxVersion is lower
                        {
                            versionMismatch = true;
                            Logger.Info("Appx Version of service is lower, upgrading SupportAssist");
                            SupportAssistProcessor.InitiateAutoUpgrade(EProcessArgument.Silent);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error(string.Format("Error occured while comparing AppxVersion {0}", ex.Message));
            }
            return versionMismatch;
        }
        //private const string SECRETKEY = "MTg1MSE8UlNBS2V5VmFsdWU+PE1vZHVsdXM+cE9QaXM1RjJ2TTdRY1VhNzJSRE5ibThSTGNSazBrQXRuc0ZiR0s2NG1PZTdTbnlxVFpwMzhmSkJJRWZVM3Y5WFU1ZjZDR1BSYmRWRlR2bFdKa1pHOFE9PTwvTW9kdWx1cz48RXhwb25lbnQ+QVFBQjwvRXhwb25lbnQ+PC9SU0FLZXlWYWx1ZT4=";
        private const string SECRETKEY = "MTE4NyE8UlNBS2V5VmFsdWU+PE1vZHVsdXM+c2oxeVJZbXdzNEhBNGNGZkwweVFSaGRmcFNFSlBmaEp0SmZ2QVFpOTVsQXQwUjFIN3ZST0lBMzQ2SzY3YnNUWm14THhjM28vWTQrSEhtTG1CUDd5K0dDMUdBdlMzMS9mQkIxYXhnbk9GUVptZlVUTlJHaVlrKzNkbnBCaSs2YXI5L29STXdvelNESjRVanhpYlp5ci9tTVM4TEdjNmpQZllnN0J4YURodS8wPTwvTW9kdWx1cz48RXhwb25lbnQ+QVFBQjwvRXhwb25lbnQ+PFA+eXJEVEZtNHJWRWFWYzl3RUg5NWlPVnhTWHovM3VZc1cvRmJsVDlOR05zQUNpSys1ZkNWM1UveUppZEV6RTJxREQvbWtGWW1yRjhtNVFGeFBGcHVpaXc9PTwvUD48UT40UjVhOTNOMUhEbGVQMEwvM09DS0kySnl2ZGw2U1dIWlQra1B5cERSc2FxNWNvdmY3ZE0waXR6UW91c0lvVVNBZHZvRHB3OHM0dFdNN2w2Ly9XWVVsdz09PC9RPjxEUD5ZQzloUEhlellCN090VmhuTEtoZmZGRHZWZndKRnFlR2xPQzNtUlh0Yi9YV1BmOEZ5b0FOREhIKzRzTy90U3NLWHY1Y2Uwd0ZRUmlkTEltaGpsejAyUT09PC9EUD48RFE+Y2sxemFzbFk0U2ZQenRjNkN2Q0hzMGU5Y3VBRjAxUzNmbmViNlFKM05ucTFCcEEyOXc2U1V4K2pYOVZ1NEZOajF3VkM3WVFyQ2xIYjZQeDdCekxacVE9PTwvRFE+PEludmVyc2VRPnVaSUREVnRVR3JuZVc3SnZJZTRtekVqK3NHTnVSZ1dMOHJVZ2luTUNENXczcWpsMVlPNmk5OUExTzdhbmcvWFRQaXRTekxUajNrelRadU9yZjRBYUZ3PT08L0ludmVyc2VRPjxEPkhsN24vTDlVYzVIVmF1SkhOTWtJQUZsMU82N2dZMFhPVVU1ZU5EL29FN2x2eFNVSEg4bFRFcFV1NTM3Mmd3NVp3ZG05ZUo3STlFNzlpQWowQnIvbWFIRTBoK1YwV1BQeXI5cGFLbHVOVExGejJhWXlOdFNLSll5aFQ4SENWODRRVVllL243aTBvL1IvcnVjRHU1YzYvUWZLOE9TRytBLzNRNGl0dkNkTytLVT08L0Q+PC9SU0FLZXlWYWx1ZT4=";

        private byte[] ValidateServerSignature(List<Arguments> arguments)
        {
            System.Diagnostics.Debugger.Launch();
            System.Diagnostics.Debugger.Break();

            try
            {
                string validateSeverHash = arguments[0].Value;
                //string privateKey = ConfigurationManager.AppSettings["PrivateKey"].ToString();
                //Decrypt the response recieved from client and then encrypt back again with server key so that client can identify the server identity.
                //string decryptResponse = AsymmetricEncryptionDecrpytion.DecryptDataInString(privateKey, validateSeverHash);
                string decryptResponse = AsymmetricEncryptionDecrpytion.DecryptDataInString(SECRETKEY, validateSeverHash);

                return AsymmetricEncryptionDecrpytion.SignData(decryptResponse, SECRETKEY);

            }
            catch (Exception ex)
            {
                Logger.Error(string.Format("Error while validating fallback signature: " + ex.Message));
            }
            return new byte[0];
        }

        private string GetLaunchArguments(ClientSource clientSource,bool versionMismatch)
        {
            Logger.Info("Inside GetLaunchArguments");
            var isCommercial = DataAccess.GetAppSettingValue(EAppSetting.IsCommercial);
            string arguments = string.Empty;
            try
            {
                LaunchArgs launchArguments = new LaunchArgs();
                launchArguments.Language = SupportAssistProcessor.GetLanguage();
                Logger.Info("language" + launchArguments.Language);
                DeviceDetails deviceDetails = SupportAssistProcessor.GetDeviceDetails();
                launchArguments.Platform = deviceDetails != null ? deviceDetails.Platform : string.Empty;
                Logger.Info("deviceDetails" + deviceDetails);
                launchArguments.Version = SupportAssistProcessor.GetInstalledVersion();
                Logger.Info("version" + launchArguments.Version);
                launchArguments.Language = string.IsNullOrEmpty(launchArguments.Language) ? "en-US" : launchArguments.Language;
                Logger.Info("language" + launchArguments.Language);
                launchArguments.VersionMismatch = versionMismatch.ToString();
                launchArguments.PrivacySetting = (!string.IsNullOrEmpty(DataAccess.GetAppSettingValue(EAppSetting.PrivacyDownload))) ? DataAccess.GetAppSettingValue(EAppSetting.PrivacyDownload) : "True";
                launchArguments.SessionSignature = WorkflowSessionManager.GetSessionSignature(clientSource);
                Logger.Info("SessionSignature" + launchArguments.SessionSignature);

                var is10SValue = DataAccess.GetAppSettingValue(EAppSetting.Is10S);
                launchArguments.Is10S = string.IsNullOrEmpty(is10SValue) ? "false" : is10SValue;

                launchArguments.IsConsumer = (string.IsNullOrEmpty(isCommercial) || !Convert.ToBoolean(isCommercial)).ToString();
                launchArguments.IsAdmin = Convert.ToString(SupportAssistCommonUtility.CheckAdminPrivilegesForCurrentUser() || !SystemUtility.IsUWPOS());
                launchArguments.IsUIRunning = WorkflowEngineManager.SessionManager.GetWebSocketSessionId(ClientSource.UI) == null ? "false" : "true";

                Logger.Info("Intial Info" + launchArguments.SessionSignature + "|" + launchArguments.Language + "|" + deviceDetails?.Platform + "|" + launchArguments.Version + "|" + launchArguments.VersionMismatch + "|" + launchArguments.PrivacySetting + "|" + launchArguments.IsAdmin + " 10S " + launchArguments.Is10S + " isConsumer " + launchArguments.IsConsumer.ToString() + " isUiRunning " + launchArguments.IsUIRunning.ToString());
                launchArguments.RequiredAppxVersion = DataAccess.IsWin10S ? ApplicationConstant.AppxVersionBackwardCompt : ApplicationConstant.AppxVersion;
                launchArguments.OSLocale = SupportAssistCommonUtility.GetOSLocale();
                launchArguments.WSPort = ApplicationConstant.WebSocketPort.ToString();
                launchArguments.CertificatePath = CertificateUtility.GetCertificate();
                launchArguments.InstallDir = DataAccess.GetInstallationDir();
                launchArguments.CommonAppDataPath = ApplicationConfiguration.AppSettings.SupportAssistAppDataPath;
                launchArguments.IsTDScanInProgress = SupportAssistRemoteCommandProcessor.IsActiveOrPassiveRemoteSessionInProgress().ToString();
                launchArguments.CanShowUserInterface = SupportAssistProcessor.IsShowUserInterface();

                arguments = JsonConvert.SerializeObject(launchArguments);
            }
            catch (Exception ex)
            {
                Logger.Error("Exception occured while getting Launch Arguments: " + ex.StackTrace);
                return REQUESTERROR;
            }
            return arguments;

        }

        private string SaveProxy(List<Arguments> arguments)
        {
            try
            {
                if (arguments.Count == 1)
                {
                    Proxy proxyDetails = new Proxy();
                    JavaScriptSerializer objSerializer = new JavaScriptSerializer();
                    proxyDetails = objSerializer.Deserialize<Proxy>(arguments[0].Value.ToString());
                }
                SupportAssistProcessor.SaveProxy(arguments[0].Value.ToString());
            }
            catch (Exception)
            {
            }
            return successString;
        }

        private string GetInstallerInitialSettings()
        {
            var settings = string.Empty;
            try
            {
                List<Arguments> listArgs = new List<Arguments>();
                Arguments userProxy = new Arguments { Key = "UserProxy", Value = SupportAssistProcessor.GetUserProxyAsString() };
                Arguments privacySetting = new Arguments { Key = "privacySetting", Value = PrivacySettingsCheck.GetDownloadSettings().ToString() };
                listArgs.Add(userProxy);
                listArgs.Add(privacySetting);
                ClientRequest clientRequest = new ClientRequest { Response = listArgs };

                settings = new JavaScriptSerializer().Serialize(clientRequest).ToString();
            }
            catch (Exception ex)
            {
                Logger.Error($"Exception occured while getting Installer Initial Settings: {ex}");
            }
            return settings;
        }

        private string DeRegisterWithPhomeServer()
        {
            ApplicationConstant.UninstallTriggered = true;
            SupportAssistProcessor.DeRegisterFromServer();
            return successString;
        }

        private ClientSource GetClientSurce(ClientRequest clientRequest)
        {
            ClientSource defaultsource = ClientSource.UI;
            Arguments args = null;

            //Take the first Arguments in case more than one provided
            if (clientRequest.RequestArguments != null && clientRequest.RequestArguments.Count > 0)
                args = clientRequest.RequestArguments.Find(x => x.Key == "ClientSource");
            if (args != null)
            {
                Enum.TryParse(args.Value, out ClientSource requiredSource);
                defaultsource = requiredSource;
            }

            return defaultsource;
        }
        #endregion

    }
}