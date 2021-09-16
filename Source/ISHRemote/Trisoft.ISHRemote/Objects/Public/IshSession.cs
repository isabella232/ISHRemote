/*
* Copyright (c) 2014 All Rights Reserved by the SDL Group.
* 
* Licensed under the Apache License, Version 2.0 (the "License");
* you may not use this file except in compliance with the License.
* You may obtain a copy of the License at
* 
*     http://www.apache.org/licenses/LICENSE-2.0
* 
* Unless required by applicable law or agreed to in writing, software
* distributed under the License is distributed on an "AS IS" BASIS,
* WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
* See the License for the specific language governing permissions and
* limitations under the License.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.ServiceModel.Security;
using System.Security.Cryptography.X509Certificates;
using System.ServiceModel;

using Trisoft.ISHRemote.HelperClasses;
using Trisoft.ISHRemote.Interfaces;
using Trisoft.ISHRemote.Application25ServiceReference;
using Trisoft.ISHRemote.Folder25ServiceReference;
using Trisoft.ISHRemote.Settings25ServiceReference;
using Trisoft.ISHRemote.User25ServiceReference;

namespace Trisoft.ISHRemote.Objects.Public
{
    /// <summary>
    /// <para type="description">Client session object to the InfoShare server instance required for every remote operation as it holds the web service url and authentication.</para>
    /// <para type="description">Furthermore it tracks your security token, provides direct client access to the web services API.</para>
    /// <para type="description">Gives access to contract parameters like separators, date formats, batch and chunk sizes.</para>
    /// </summary>
    public class IshSession : IDisposable
    {
        private readonly ILogger _logger;

        private readonly Uri _webServicesBaseUri;
        private IshConnectionConfiguration _ishConnectionConfiguration;
        private string _ishUserName;
        private string _userName;
        private string _userLanguage;
        private readonly string _ishPassword;
        private readonly string _separator = ", ";
        private readonly string _folderPathSeparator = @"\";
        
        /// <summary>
        /// AuthenticationContext is accessible for the ASMX web services to pass by ref, in turn it is refreshed per API call
        /// </summary>
        internal string _authenticationContext;

        private IshVersion _serverVersion;
        private IshVersion _clientVersion;
        private IshTypeFieldSetup _ishTypeFieldSetup;
        private Enumerations.StrictMetadataPreference _strictMetadataPreference = Enumerations.StrictMetadataPreference.Continue;
        private NameHelper _nameHelper;
        private Enumerations.PipelineObjectPreference _pipelineObjectPreference = Enumerations.PipelineObjectPreference.PSObjectNoteProperty;
        private Enumerations.RequestedMetadataGroup _defaultRequestedMetadata = Enumerations.RequestedMetadataGroup.Basic;

        /// <summary>
        /// Used by the SOAP API that retrieves files/blobs in multiple chunk, this parameter is the chunksize (10485760 bytes is 10Mb)
        /// </summary>
        private int _chunkSize = 10485760;
        /// <summary>
        /// Used to divide bigger data set retrievals in multiple API calls, 999 is the best optimization server-side (Oracle IN-clause only allows 999 values, so 1000 would mean 2x queries server-side)
        /// </summary>
        private int _metadataBatchSize = 999;
        private int _blobBatchSize = 50;
        private TimeSpan _timeout = new TimeSpan(0, 0, 20);  // up to 15s for a DNS lookup according to https://msdn.microsoft.com/en-us/library/system.net.http.httpclient.timeout%28v=vs.110%29.aspx
        private readonly bool _ignoreSslPolicyErrors = false;

        // one HttpClient per IshSession with potential certificate overwrites which can be reused across requests
        private readonly HttpClient _httpClient;

        //private Annotation25ServiceReference.Annotation _annotation25;
        private Application25ServiceReference.Application25Soap _application25;
        //private DocumentObj25ServiceReference.DocumentObj _documentObj25;
        private Folder25ServiceReference.Folder25Soap _folder25;
        private User25ServiceReference.User25Soap _user25;
        //private UserRole25ServiceReference.UserRole _userRole25;
        //private UserGroup25ServiceReference.UserGroup _userGroup25;
        //private ListOfValues25ServiceReference.ListOfValues _listOfValues25;
        //private PublicationOutput25ServiceReference.PublicationOutput _publicationOutput25;
        //private OutputFormat25ServiceReference.OutputFormat _outputFormat25;
        private Settings25ServiceReference.Settings25Soap _settings25;
        //private EDT25ServiceReference.EDT _EDT25;
        //private EventMonitor25ServiceReference.EventMonitor _eventMonitor25;
        //private Baseline25ServiceReference.Baseline _baseline25;
        //private MetadataBinding25ServiceReference.MetadataBinding _metadataBinding25;
        //private Search25ServiceReference.Search _search25;
        //private TranslationJob25ServiceReference.TranslationJob _translationJob25;
        //private TranslationTemplate25ServiceReference.TranslationTemplate _translationTemplate25;
        //private BackgroundTask25ServiceReference.BackgroundTask _backgroundTask25;

        /// <summary>
        /// Creates a session object holding contracts and proxies to the web services API. Takes care of username/password and 'Active Directory' authentication (NetworkCredential) to the Secure Token Service.
        /// </summary>
        /// <param name="logger">Instance of the ILogger interface to allow some logging although Write-* is not very thread-friendly.</param>
        /// <param name="webServicesBaseUrl">The url to the web service API. For example 'https://example.com/ISHWS/'</param>
        /// <param name="ishUserName">InfoShare user name. For example 'Admin'</param>
        /// <param name="ishPassword">Matching password as SecureString of the incoming user name. When null is provided, a NetworkCredential() is created instead.</param>
        /// <param name="timeout">Timeout to control Send/Receive timeouts of HttpClient when downloading content like connectionconfiguration.xml</param>
        /// <param name="ignoreSslPolicyErrors">IgnoreSslPolicyErrors presence indicates that a custom callback will be assigned to ServicePointManager.ServerCertificateValidationCallback. Defaults false of course, as this is creates security holes! But very handy for Fiddler usage though.</param>
        public IshSession(ILogger logger, string webServicesBaseUrl, string ishUserName, string ishPassword, TimeSpan timeout, bool ignoreSslPolicyErrors)
        {
            _logger = logger;
            
            _ignoreSslPolicyErrors = ignoreSslPolicyErrors;
            HttpClientHandler handler = new HttpClientHandler();
            _logger.WriteDebug($"Enabling Tls, Tls11, Tls12 and Tls13 security protocols Timeout[{_timeout}] IgnoreSslPolicyErrors[{_ignoreSslPolicyErrors}]");
            if (_ignoreSslPolicyErrors)
            {
                // ISHRemote 0.x used CertificateValidationHelper.OverrideCertificateValidation which only works on net48 and overwrites the full AppDomain,
                // below solution is cleaner for HttpHandler (so connectionconfiguration.xml and future OpenAPI) and SOAP proxies use factory.Credentials.ServiceCertificate.SslCertificateAuthentication
                //CertificateValidationHelper.OverrideCertificateValidation();
                // overwrite certificate handling for HttpClient requests
                handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
            }
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;
            handler.SslProtocols = (System.Security.Authentication.SslProtocols)(SecurityProtocolType.Tls | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13);
            _httpClient = new HttpClient(handler);
            _httpClient.Timeout = _timeout;

            // webServicesBaseUrl should have trailing slash, otherwise .NET throws unhandy "Reference to undeclared entity 'raquo'." error
            _webServicesBaseUri = (webServicesBaseUrl.EndsWith("/")) ? new Uri(webServicesBaseUrl) : new Uri(webServicesBaseUrl + "/");
            _ishUserName = ishUserName == null ? Environment.UserName : ishUserName;
            _ishPassword = ishPassword;
            _timeout = timeout;
            LoadConnectionConfiguration();
            CreateConnection();
        }

        private void LoadConnectionConfiguration()
        {
            var connectionConfigurationUri = new Uri(_webServicesBaseUri, "connectionconfiguration.xml");
            _logger.WriteDebug($"LoadConnectionConfiguration uri[{connectionConfigurationUri}] timeout[{_httpClient.Timeout}]");
            var responseMessage = _httpClient.GetAsync(connectionConfigurationUri).GetAwaiter().GetResult();
            string response = responseMessage.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            _ishConnectionConfiguration = new IshConnectionConfiguration(response);
            _logger.WriteDebug($"LoadConnectionConfiguration found InfoShareWSUrl[{_ishConnectionConfiguration.InfoShareWSUrl}] ApplicationName[{_ishConnectionConfiguration.ApplicationName}] SoftwareVersion[{_ishConnectionConfiguration.SoftwareVersion}]");
            if (_ishConnectionConfiguration.InfoShareWSUrl != _webServicesBaseUri)
            {
                _logger.WriteDebug($"LoadConnectionConfiguration noticed incoming _webServicesBaseUri[{_webServicesBaseUri}] differs from _ishConnectionConfiguration.InfoShareWSUrl[{_ishConnectionConfiguration.InfoShareWSUrl}]. Using _webServicesBaseUri.");
            }
        }

        private void CreateConnection()
        {
            // Before ISHRemotev7+ there was username/password and Active Directory authentication, where the logic came down to:
            // Credential = _ishPassword == null ? null : new NetworkCredential(_ishUserName, SecureStringConversions.SecureStringToString(_ishPassword)),

            // application proxy to get server version or authentication context init is a must as it also confirms credentials, can take up to 1s
            _logger.WriteDebug("CreateConnection Application25.Login");
            var response = Application25.Login(new LoginRequest()
            {
                psApplication = _ishConnectionConfiguration.ApplicationName,
                psUserName = _ishUserName,
                psPassword = _ishPassword,
                psOutAuthContext = _authenticationContext
            });
            _authenticationContext = response.psOutAuthContext;
            //Application25.Login(_ishConnectionConfiguration.ApplicationName, _ishUserName, _ishPassword, ref _authenticationContext);
            _logger.WriteDebug("CreateConnection Application25.GetVersion");
            _serverVersion = new IshVersion(Application25.GetVersion());
        }

        internal IshTypeFieldSetup IshTypeFieldSetup
        {
            get
            {
                if (_ishTypeFieldSetup == null)
                {
                    if (_serverVersion.MajorVersion >= 13) 
                    {
                        _logger.WriteDebug($"Loading Settings25.RetrieveFieldSetupByIshType...");
                        string xmlTypeFieldSetup;
                        var response = Settings25.RetrieveFieldSetupByIshType(new RetrieveFieldSetupByIshTypeRequest() {
                            psAuthContext = _authenticationContext,
                            pasIshTypes = null
                        });
                        _authenticationContext = response.psAuthContext;
                        xmlTypeFieldSetup = response.psOutXMLFieldSetup;
                        _ishTypeFieldSetup = new IshTypeFieldSetup(_logger, xmlTypeFieldSetup);
                        _ishTypeFieldSetup.StrictMetadataPreference = _strictMetadataPreference;
                    }
                    else
                    {
                        _logger.WriteDebug($"Loading TriDKXmlSetupFullExport_12_00_01...");
                        var triDKXmlSetupHelper = new TriDKXmlSetupHelper(_logger, Properties.Resouces.ISHTypeFieldSetup.TriDKXmlSetupFullExport_12_00_01);
                        _ishTypeFieldSetup = new IshTypeFieldSetup(_logger, triDKXmlSetupHelper.IshTypeFieldDefinition);
                        _ishTypeFieldSetup.StrictMetadataPreference = Enumerations.StrictMetadataPreference.Off;    // Otherwise custom metadata fields are always removed as they are unknown for the default TriDKXmlSetup Resource
                    }

                    if (_serverVersion.MajorVersion == 13 || (_serverVersion.MajorVersion == 14 && _serverVersion.RevisionVersion < 4))
                    {
                        // Loading/Merging Settings ISHMetadataBinding for 13/13.0.0 up till 14SP4/14.0.4 setup
                        // Note that IMetadataBinding was introduced in 2016/12.0.0 but there was no dynamic FieldSetup retrieval
                        // Passing IshExtensionConfig object to IshTypeFieldSetup constructor
                        _logger.WriteDebug($"Loading Settings25.GetMetadata for field[" + FieldElements.ExtensionConfiguration + "]...");
                        IshFields metadata = new IshFields();
                        metadata.AddField(new IshRequestedMetadataField(FieldElements.ExtensionConfiguration, Enumerations.Level.None, Enumerations.ValueType.Value));  // do not pass over IshTypeFieldSetup.ToIshRequestedMetadataFields, as we are initializing that object
                        string xmlIshObjects = "";
                        var response = Settings25.GetMetaData(new Settings25ServiceReference.GetMetaDataRequest()
                        {
                            psAuthContext = _authenticationContext,
                            psXMLRequestedMetaData = metadata.ToXml(),
                            psOutXMLObjList = xmlIshObjects
                        });
                        _authenticationContext = response.psAuthContext;
                        xmlIshObjects = response.psOutXMLObjList;
                        var ishFields = new IshObjects(xmlIshObjects).Objects[0].IshFields;
                        string xmlSettingsExtensionConfig = ishFields.GetFieldValue(FieldElements.ExtensionConfiguration, Enumerations.Level.None, Enumerations.ValueType.Value);
                        IshSettingsExtensionConfig.MergeIntoIshTypeFieldSetup(_logger, _ishTypeFieldSetup, xmlSettingsExtensionConfig);
                    }
                    
                }
                return _ishTypeFieldSetup;
            }
        }

        internal NameHelper NameHelper
        {
            get
            {
                if (_nameHelper == null)
                {
                    _nameHelper = new NameHelper(this);
                }
                return _nameHelper;
            }
        }

        public string WebServicesBaseUrl
        {
            get { return _webServicesBaseUri.ToString(); }
        }

        /// <summary>
        /// The user name used to authenticate to the service, is initialized to Environment.UserName in case of Windows Authentication through NetworkCredential()
        /// </summary>
        public string IshUserName
        {
            get { return _ishUserName; }
            set { _ishUserName = value; }
        }

        internal string Name
        {
            get { return $"[{WebServicesBaseUrl}][{IshUserName}]"; }
        }

        /// <summary>
        /// The user name as available on the InfoShare User Profile in the CMS under field 'USERNAME'
        /// </summary>
        public string UserName
        {
            get
            {
                if (_userName == null)
                {
                    //TODO [Could] IshSession could initialize the current IshUser completely based on all available user metadata and store it on the IshSession
                    string requestedMetadata = "<ishfields><ishfield name='USERNAME' level='none'/></ishfields>";
                    string xmlIshObjects = "";
                    var response = User25.GetMyMetaData(new GetMyMetaDataRequest() { 
                        psAuthContext = _authenticationContext, 
                        psXMLRequestedMetaData= requestedMetadata, 
                        psOutXMLObjList = xmlIshObjects });
                    _authenticationContext = response.psAuthContext;
                    xmlIshObjects = response.psOutXMLObjList;
                    Enumerations.ISHType[] ISHType = { Enumerations.ISHType.ISHUser };
                    IshObjects ishObjects = new IshObjects(ISHType, xmlIshObjects);
                    _userName = ishObjects.Objects[0].IshFields.GetFieldValue("USERNAME", Enumerations.Level.None, Enumerations.ValueType.Value);
                }
                return _userName;
            }
        }

        /// <summary>
        /// The user language as available on the InfoShare User Profile in the CMS under field 'FISHUSERLANGUAGE'
        /// </summary>
        public string UserLanguage
        {
            get
            {
                if (_userLanguage == null)
                {
                    //TODO [Could] IshSession could initialize the current IshUser completely based on all available user metadata and store it on the IshSession
                    string requestedMetadata = "<ishfields><ishfield name='FISHUSERLANGUAGE' level='none'/></ishfields>";
                    string xmlIshObjects = "";
                    var response = User25.GetMyMetaData(new GetMyMetaDataRequest() { 
                        psAuthContext = _authenticationContext, 
                        psXMLRequestedMetaData = requestedMetadata,
                        psOutXMLObjList = xmlIshObjects });
                    _authenticationContext = response.psAuthContext;
                    xmlIshObjects = response.psOutXMLObjList;
                    Enumerations.ISHType[] ISHType = { Enumerations.ISHType.ISHUser };
                    IshObjects ishObjects = new IshObjects(ISHType, xmlIshObjects);
                    _userLanguage = ishObjects.Objects[0].IshFields.GetFieldValue("FISHUSERLANGUAGE", Enumerations.Level.None, Enumerations.ValueType.Value);
                }
                return _userLanguage;
            }
        }

        internal IshVersion ServerIshVersion
        {
            get { return _serverVersion; }
        }

        public string ServerVersion
        {
            get { return _serverVersion.ToString(); }
        }

        /// <summary>
        /// Retrieving assembly file version, actually can take up to 500 ms to get this initialized, so moved code to JIT property
        /// </summary>
        internal IshVersion ClientIshVersion
        {
            get
            {
                if (_clientVersion == null)
                {
                    _clientVersion = new IshVersion(FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location).FileVer‌​sion);
                }
                return _clientVersion;
            }
        }
        
        public string ClientVersion
        {
            get { return ClientIshVersion.ToString(); }
        }

        public List<IshTypeFieldDefinition> IshTypeFieldDefinition
        {
            get
            {
                return IshTypeFieldSetup.IshTypeFieldDefinition;
            }
            internal set
            {
                _ishTypeFieldSetup = new IshTypeFieldSetup(_logger, value);
            }
        }

        public string AuthenticationContext
        {
            get
            {
                return _authenticationContext;
            }
        }

        public string Separator
        {
            get { return _separator; }
        }

        public string FolderPathSeparator
        {
            get { return _folderPathSeparator; }
        }

        /// <summary>
        /// Timeout to control Send/Receive timeouts of HttpClient when downloading content like connectionconfiguration.xml
        /// </summary>
        public TimeSpan Timeout
        {
            get { return _timeout; }
            set { _timeout = value; }
        }

        /// <summary>
        /// Web Service Retrieve batch size, if implemented, expressed in number of Ids/Objects for usage in metadata calls
        /// </summary>
        public int MetadataBatchSize
        {
            get { return _metadataBatchSize; }
            set { _metadataBatchSize = (value > 0) ? value : 999; }
        }

        /// <summary>
        /// Client side filtering of nonexisting or unallowed metadata can be done silently, with warning or not at all. 
        /// </summary>
        public Enumerations.StrictMetadataPreference StrictMetadataPreference
        {
            get { return _strictMetadataPreference; }
            set
            {
                _strictMetadataPreference = value;
                IshTypeFieldSetup.StrictMetadataPreference = value;
            }
        }

        /// <summary>
        /// Allows tuning client-side object enrichment like no wrapping (off) or PSObject-with-PSNoteProperty wrapping.
        /// </summary>
        public Enumerations.PipelineObjectPreference PipelineObjectPreference
        {
            get { return _pipelineObjectPreference; }
            set { _pipelineObjectPreference = value; }
        }

        /// <summary>
        /// Any RequestedMetadata will be preloaded with the Descriptive/Basic/All metadata fields known for the ISHType[] in use by the cmdlet
        /// A potential override/append by the specified -RequestedMetadata is possible.
        /// </summary>
        public Enumerations.RequestedMetadataGroup DefaultRequestedMetadata
        {
            get { return _defaultRequestedMetadata; }
            set { _defaultRequestedMetadata = value; }
        }

        /// <summary>
        /// Web Service Retrieve batch size, if implemented, expressed in number of Ids/Objects for usage in blob/ishdata calls
        /// </summary>
        public int BlobBatchSize
        {
            get { return _blobBatchSize; }
            set { _blobBatchSize = value; }
        }

        /// <summary>
        /// Web Service Retrieve chunk size, if implemented, expressed in bytes
        /// </summary>
        public int ChunkSize
        {
            get { return _chunkSize; }
            set { _chunkSize = value; }
        }

         #region Web Services Getters

        //public Annotation25ServiceReference.Annotation Annotation25
        //{
        //    get
        //    {
        //        VerifyTokenValidity();

        //        if (_annotation25 == null)
        //        {
        //            _annotation25 = _connection.GetAnnotation25Channel();
        //        }
        //        return _annotation25;
        //    }
        //}

        internal Application25ServiceReference.Application25Soap Application25
        {
            get
            {
                //TODO [Must] ISHRemotev7+ Cleanup// VerifyTokenValidity();

                if (_application25 == null)
                {
                    // Create HTTP Binding Objects
                    BasicHttpBinding binding = new BasicHttpBinding();
                    binding.MaxBufferSize = int.MaxValue;
                    binding.ReaderQuotas = System.Xml.XmlDictionaryReaderQuotas.Max;
                    binding.MaxReceivedMessageSize = int.MaxValue;
                    binding.AllowCookies = true;
                    binding.Security.Mode = System.ServiceModel.BasicHttpSecurityMode.Transport;
                    // Building Terminal Point Objects Based on Web Service URLs
                    EndpointAddress endpoint = new EndpointAddress(new Uri(_webServicesBaseUri, "application25.asmx").AbsoluteUri);
                    // Create a factory that calls interfaces. Note that generics can only pass in interfaces here
                    var factory = new ChannelFactory<Application25Soap>(binding, endpoint);
                    // Get specific invocation instances from the factory
                    if (_ignoreSslPolicyErrors)
                    { 
                        factory.Credentials.ServiceCertificate.SslCertificateAuthentication = new X509ServiceCertificateAuthentication()
                        {
                            CertificateValidationMode = X509CertificateValidationMode.None,
                            RevocationMode = X509RevocationMode.NoCheck
                        };
                    }
                    _application25 = factory.CreateChannel();
                }
                return _application25;
            }
        }

        internal User25ServiceReference.User25Soap User25
        {
            get
            {
                //TODO [Must] ISHRemotev7+ Cleanup// VerifyTokenValidity();

                if (_user25 == null)
                {
                    // Create HTTP Binding Objects
                    BasicHttpBinding binding = new BasicHttpBinding();
                    binding.MaxBufferSize = int.MaxValue;
                    binding.ReaderQuotas = System.Xml.XmlDictionaryReaderQuotas.Max;
                    binding.MaxReceivedMessageSize = int.MaxValue;
                    binding.AllowCookies = true;
                    binding.Security.Mode = System.ServiceModel.BasicHttpSecurityMode.Transport;
                    // Building Terminal Point Objects Based on Web Service URLs
                    EndpointAddress endpoint = new EndpointAddress(new Uri(_webServicesBaseUri, "user25.asmx").AbsoluteUri);
                    // Create a factory that calls interfaces. Note that generics can only pass in interfaces here
                    var factory = new ChannelFactory<User25Soap>(binding, endpoint);
                    // Get specific invocation instances from the factory
                    if (_ignoreSslPolicyErrors)
                    {
                        factory.Credentials.ServiceCertificate.SslCertificateAuthentication = new X509ServiceCertificateAuthentication()
                        {
                            CertificateValidationMode = X509CertificateValidationMode.None,
                            RevocationMode = X509RevocationMode.NoCheck
                        };
                    }
                    _user25 = factory.CreateChannel();
                }
                return _user25;
            }
        }

        //public UserRole25ServiceReference.UserRole UserRole25
        //{
        //    get
        //    {
        //        VerifyTokenValidity();

        //        if (_userRole25 == null)
        //        {
        //            _userRole25 = _connection.GetUserRole25Channel();
        //        }
        //        return _userRole25;
        //    }
        //}

        //public UserGroup25ServiceReference.UserGroup UserGroup25
        //{
        //    get
        //    {
        //        VerifyTokenValidity();

        //        if (_userGroup25 == null)
        //        {
        //            _userGroup25 = _connection.GetUserGroup25Channel();
        //        }
        //        return _userGroup25;
        //    }
        //}

        //public DocumentObj25ServiceReference.DocumentObj DocumentObj25
        //{
        //    get
        //    {
        //        VerifyTokenValidity();

        //        if (_documentObj25 == null)
        //        {
        //            _documentObj25 = _connection.GetDocumentObj25Channel();
        //        }
        //        return _documentObj25;
        //    }
        //}

        //public PublicationOutput25ServiceReference.PublicationOutput PublicationOutput25
        //{
        //    get
        //    {
        //        VerifyTokenValidity();

        //        if (_publicationOutput25 == null)
        //        {
        //            _publicationOutput25 = _connection.GetPublicationOutput25Channel();
        //        }
        //        return _publicationOutput25;
        //    }
        //}

        internal Settings25ServiceReference.Settings25Soap Settings25
        {
            get
            {
                //TODO [Must] ISHRemotev7+ Cleanup// VerifyTokenValidity();

                if (_settings25 == null)
                {
                    // Create HTTP Binding Objects
                    BasicHttpBinding binding = new BasicHttpBinding();
                    binding.MaxBufferSize = int.MaxValue;
                    binding.ReaderQuotas = System.Xml.XmlDictionaryReaderQuotas.Max;
                    binding.MaxReceivedMessageSize = int.MaxValue;
                    binding.AllowCookies = true;
                    binding.Security.Mode = System.ServiceModel.BasicHttpSecurityMode.Transport;
                    // Building Terminal Point Objects Based on Web Service URLs
                    EndpointAddress endpoint = new EndpointAddress(new Uri(_webServicesBaseUri, "settings25.asmx").AbsoluteUri);
                    // Create a factory that calls interfaces. Note that generics can only pass in interfaces here
                    var factory = new ChannelFactory<Settings25Soap>(binding, endpoint);
                    // Get specific invocation instances from the factory
                    if (_ignoreSslPolicyErrors)
                    {
                        factory.Credentials.ServiceCertificate.SslCertificateAuthentication = new X509ServiceCertificateAuthentication()
                        {
                            CertificateValidationMode = X509CertificateValidationMode.None,
                            RevocationMode = X509RevocationMode.NoCheck
                        };
                    }
                    _settings25 = factory.CreateChannel();
                }
                return _settings25;
            }
        }

        //public EventMonitor25ServiceReference.EventMonitor EventMonitor25
        //{
        //    get
        //    {
        //        VerifyTokenValidity();

        //        if (_eventMonitor25 == null)
        //        {
        //            _eventMonitor25 = _connection.GetEventMonitor25Channel();
        //        }
        //        return _eventMonitor25;
        //    }
        //}

        //public Baseline25ServiceReference.Baseline Baseline25
        //{
        //    get
        //    {
        //        VerifyTokenValidity();

        //        if (_baseline25 == null)
        //        {
        //            _baseline25 = _connection.GetBaseline25Channel();
        //        }
        //        return _baseline25;
        //    }
        //}

        //public MetadataBinding25ServiceReference.MetadataBinding MetadataBinding25
        //{
        //    get
        //    {
        //        VerifyTokenValidity();

        //        if (_metadataBinding25 == null)
        //        {
        //            _metadataBinding25 = _connection.GetMetadataBinding25Channel();
        //        }
        //        return _metadataBinding25;
        //    }
        //}

        internal Folder25ServiceReference.Folder25Soap Folder25
        {
            get
            {
                //TODO [Must] ISHRemotev7+ Cleanup// VerifyTokenValidity();

                if (_folder25 == null)
                {
                    // Create HTTP Binding Objects
                    BasicHttpBinding binding = new BasicHttpBinding();
                    binding.MaxBufferSize = int.MaxValue;
                    binding.ReaderQuotas = System.Xml.XmlDictionaryReaderQuotas.Max;
                    binding.MaxReceivedMessageSize = int.MaxValue;
                    binding.AllowCookies = true;
                    binding.Security.Mode = System.ServiceModel.BasicHttpSecurityMode.Transport;
                    // Building Terminal Point Objects Based on Web Service URLs
                    EndpointAddress endpoint = new EndpointAddress(new Uri(_webServicesBaseUri, "folder25.asmx").AbsoluteUri);
                    // Create a factory that calls interfaces. Note that generics can only pass in interfaces here
                    var factory = new ChannelFactory<Folder25Soap>(binding, endpoint);
                    // Get specific invocation instances from the factory
                    if (_ignoreSslPolicyErrors)
                    {
                        factory.Credentials.ServiceCertificate.SslCertificateAuthentication = new X509ServiceCertificateAuthentication()
                        {
                            CertificateValidationMode = X509CertificateValidationMode.None,
                            RevocationMode = X509RevocationMode.NoCheck
                        };
                    }
                    _folder25 = factory.CreateChannel();
                }
                return _folder25;
            }
        }

        //public ListOfValues25ServiceReference.ListOfValues ListOfValues25
        //{
        //    get
        //    {
        //        VerifyTokenValidity();

        //        if (_listOfValues25 == null)
        //        {
        //            _listOfValues25 = _connection.GetListOfValues25Channel();
        //        }
        //        return _listOfValues25;
        //    }
        //}

        //public OutputFormat25ServiceReference.OutputFormat OutputFormat25
        //{
        //    get
        //    {
        //        VerifyTokenValidity();

        //        if (_outputFormat25 == null)
        //        {
        //            _outputFormat25 = _connection.GetOutputFormat25Channel();
        //        }
        //        return _outputFormat25;
        //    }
        //}

        //public EDT25ServiceReference.EDT EDT25
        //{
        //    get
        //    {
        //        VerifyTokenValidity();

        //        if (_EDT25 == null)
        //        {
        //            _EDT25 = _connection.GetEDT25Channel();
        //        }
        //        return _EDT25;
        //    }
        //}

        //public TranslationJob25ServiceReference.TranslationJob TranslationJob25
        //{
        //    get
        //    {
        //        VerifyTokenValidity();

        //        if (_translationJob25 == null)
        //        {
        //            _translationJob25 = _connection.GetTranslationJob25Channel();
        //        }
        //        return _translationJob25;
        //    }
        //}

        //public TranslationTemplate25ServiceReference.TranslationTemplate TranslationTemplate25
        //{
        //    get
        //    {
        //        VerifyTokenValidity();

        //        if (_translationTemplate25 == null)
        //        {
        //            _translationTemplate25 = _connection.GetTranslationTemplate25Channel();
        //        }
        //        return _translationTemplate25;
        //    }
        //}

        //public Search25ServiceReference.Search Search25
        //{
        //    get
        //    {
        //        VerifyTokenValidity();

        //        if (_search25 == null)
        //        {
        //            _search25 = _connection.GetSearch25Channel();
        //        }
        //        return _search25;
        //    }
        //}

        //public BackgroundTask25ServiceReference.BackgroundTask BackgroundTask25
        //{
        //    get
        //    {
        //        VerifyTokenValidity();

        //        if (_backgroundTask25 == null)
        //        {
        //            _backgroundTask25 = _connection.GetBackgroundTask25Channel();
        //        }
        //        return _backgroundTask25;
        //    }
        //}

        #endregion

        //TODO [Must] ISHRemotev7+ Cleanup
        //private void VerifyTokenValidity()
        //{
        //    if (_connection.IsValid) return;

        //    // Not valid...
        //    // ...dispose connection
        //    _connection.Dispose();
        //    // ...discard all channels
        //    _application25 = null;
        //    _baseline25 = null;
        //    _documentObj25 = null;
        //    _EDT25 = null;
        //    _eventMonitor25 = null;
        //    _folder25 = null;
        //    _listOfValues25 = null;
        //    _metadataBinding25 = null;
        //    _outputFormat25 = null;
        //    _publicationOutput25 = null;
        //    _search25 = null;
        //    _settings25 = null;
        //    _translationJob25 = null;
        //    _translationTemplate25 = null;
        //    _user25 = null;
        //    _userGroup25 = null;
        //    _userRole25 = null;
        //    // ...and re-create connection
        //    CreateConnection();
        //}

        public void Dispose()
        {
        }
        public void Close()
        {
            Dispose();
        }
    }
}
