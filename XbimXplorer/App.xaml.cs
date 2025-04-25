#region XbimHeader

// The eXtensible Building Information Modelling (xBIM) Toolkit
// Solution:    XbimComplete
// Project:     XbimXplorer
// Filename:    App.xaml.cs
// Published:   01, 2012
// Last Edited: 9:05 AM on 20 12 2011
// (See accompanying copyright.rtf)

#endregion

#region Directives

using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Windows;
using Xbim.IO;
using XbimXplorer.Properties;
using Newtonsoft.Json;

#endregion

namespace XbimXplorer
{
    /// <summary>
    ///   Interaction logic for App.xaml
    /// </summary>
    public partial class App
    {
		private HttpListener _listener;
		private Thread _listenerThread;
		private bool _isRunning;
		private XplorerMainWindow _mainView;
		// todo: the whole concept of ContextWcsAdjustment need to be reviewed in the geometry engine.

		/// <summary>
		/// Todo, this feature has to do with the transformation of the model to 0,0,0 point of coordinate system
		/// Its use has to be consistent across the call to the XbimPlacementTree class
		/// </summary>
		public static bool ContextWcsAdjustment = true;

        /// <summary>
        /// Raises the <see cref="E:System.Windows.Application.Startup"/> event.
        /// </summary>
        /// <param name="e">A <see cref="T:System.Windows.StartupEventArgs"/> that contains the event data.</param>
        protected override void OnStartup(StartupEventArgs e)
        {
			// Start HTTP listener before main window
			StartHttpListener();

			// evaluate special parameters before loading MainWindow
			var blockPlugin = false;
            foreach (var thisArg in e.Args)
            {
                if (string.Compare("/noplugins", thisArg, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    blockPlugin = true;
                }
            }

            // see if an update of settings is required from a previous version of the app.
            // this will allow to retain the configuration across versions, it is useful for the squirrel installer
            //
            if (Settings.Default.SettingsUpdateRequired)
            {
                Settings.Default.Upgrade();
                Settings.Default.SettingsUpdateRequired = false;
                Settings.Default.Save();
            }

            _mainView = new XplorerMainWindow(blockPlugin);
            _mainView.Show();
            _mainView.DrawingControl.ViewHome();
            var bOneModelLoaded = false;
            for (var i = 0; i< e.Args.Length; i++)
            {
                var thisArg = e.Args[i];
                if (string.Compare("/AccessMode", thisArg, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    var stringMode = e.Args[++i];
                    XbimDBAccess acce;
                    if (Enum.TryParse(stringMode, out acce))
                    {
                        _mainView.FileAccessMode = acce;
                    }
                }
                else if (string.Compare("/plugin", thisArg, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    var pluginName = e.Args[++i];
                    if (File.Exists(pluginName))
                    {
                        var fi = new FileInfo(pluginName);
                        var di = fi.Directory;
                        _mainView.LoadPlugin(di, true, fi.Name);
                        continue;
                    }
                    if (Directory.Exists(pluginName) )
                    {
                        var di = new DirectoryInfo(pluginName);
                        _mainView.LoadPlugin(di, true);
                        continue;
                    }
                    Clipboard.SetText(pluginName);
                    MessageBox.Show(pluginName + " not found. The full file name has been copied to clipboard.", "Plugin not found", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                else if (string.Compare("/select", thisArg, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    var selLabel = e.Args[++i];
                    Debug.Write("Select " + selLabel + "... ");
                    _mainView.LoadingComplete += delegate
                    {
                        int iSel;
                        if (!int.TryParse(selLabel, out iSel))
                            return;
                        if (_mainView.Model == null)
                            return;
                        if (_mainView.Model.Instances[iSel] == null)
                            return;
                        _mainView.SelectedItem = _mainView.Model.Instances[iSel];    
                    };
                }
                else if (File.Exists(thisArg) && bOneModelLoaded == false)
                {
                    // does not support the load of two models
                    bOneModelLoaded = true;
                    _mainView.LoadAnyModel(thisArg);
                }
            }
        } // onStartUp

		protected override void OnExit(ExitEventArgs e)
		{
			// Clean up HTTP listener
			StopHttpListener();
			base.OnExit(e);
		}

		/// <summary>
		/// listen for HTTP requests on localhost:8188/viewer
		/// 1. GET /viewer/status - returns the status of the server
		/// 2. POST /viewer/show_model?path="c:\filename.ifc" - loads the model
		/// 3. POST /viewer/shutdown - shuts down the server
		private void StartHttpListener()
		{
			Debug.WriteLine("Starting HTTP listener...");
			_isRunning = true;
			_listener = new HttpListener();
			_listener.Prefixes.Add("http://localhost:8188/");

			Debug.WriteLine($"Listening on prefixes: {string.Join(", ", _listener.Prefixes)}");

			_listenerThread = new Thread(() =>
			{
				try
				{
					_listener.Start();
					Debug.WriteLine("HTTP listener successfully started");
					while (_isRunning)
					{
						Debug.WriteLine("Waiting for request...");
						var context = _listener.GetContext();
						Debug.WriteLine($"Request received: {context.Request.Url}");
						ThreadPool.QueueUserWorkItem(ProcessRequest, context);
					}
				}
				catch (HttpListenerException ex)
				{
					Debug.WriteLine($"Listener error: {ex.Message} (Error code: {ex.ErrorCode})");
				}
				catch (Exception ex)
				{
					Debug.WriteLine($"Unexpected listener error: {ex}");
				}
			})
			{
				IsBackground = true,
				Name = "HTTP Listener Thread"
			};
			_listenerThread.SetApartmentState(ApartmentState.STA); // Important for WPF
			_listenerThread.Start();
		}

		public bool IsListenerRunning => _listenerThread?.IsAlive == true && _isRunning;

		private void ProcessRequest(object state)
		{
			var context = (HttpListenerContext)state;
			var request = context.Request;
			var response = context.Response;
			try
			{
				string path = request.Url?.AbsolutePath ?? "";
				if (request.HttpMethod == "GET" && path.EndsWith("/viewer/status"))
				{
					// GET /viewer/status - Returns server status
					HandleStatusRequest(response);
				}
				else if (request.HttpMethod == "POST" && path.EndsWith("/viewer/show_model"))
				{
					HandleShowModelRequest(context);
				}
				else if (request.HttpMethod == "POST" && path.EndsWith("/viewer/shutdown"))
				{
					HandleShutdownRequest(response);
				}
				else
				{
					response.StatusCode = 404;
					WriteResponse(response, "Not Found");
				}
			}
			catch (Exception ex)
			{
				response.StatusCode = 500;
				WriteResponse(response, $"Error: {ex.Message}");
			}
			finally
			{
				response.Close();
			}
		}

		private void HandleStatusRequest(HttpListenerResponse response)
		{
			var status = _mainView.IsLoadingFile;
			// Return the server status as a boolean
			WriteResponse(response, status.ToString());
		}

		public class ModelLoadRequest
		{
			public string Path { get; set; }
		}
		// This method handles the POST request to load a model
		private void HandleShowModelRequest(HttpListenerContext context)
		{
			var request = context.Request;
			var response = context.Response;
			try
			{
				// 1. Verify it's a POST request
				if (request.HttpMethod != "POST")
				{
					response.StatusCode = 405; // Method Not Allowed
					WriteResponse(response, "Only POST method is supported");
					return;
				}

				// 2. Check content type
				if (!request.ContentType.StartsWith("application/json"))
				{
					response.StatusCode = 415; // Unsupported Media Type
					WriteResponse(response, "Only application/json content type is supported");
					return;
				}

				// 3. Read JSON body
				string jsonBody;
				using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
				{
					jsonBody = reader.ReadToEnd();
				}

				// 4. Parse JSON (using Newtonsoft.Json)
				var modelRequest = JsonConvert.DeserializeObject<ModelLoadRequest>(jsonBody, new JsonSerializerSettings
				{
					MissingMemberHandling = MissingMemberHandling.Error,
					NullValueHandling = NullValueHandling.Ignore,
					// Case-insensitive by default in Newtonsoft.Json
				});

				// 5. Validate path
				if (string.IsNullOrWhiteSpace(modelRequest?.Path))
				{
					response.StatusCode = 400; // Bad Request
					WriteResponse(response, "Path parameter is required in JSON body");
					return;
				}

				// 6. Verify file exists
				if (!File.Exists(modelRequest.Path))
				{
					response.StatusCode = 404; // Not Found
					WriteResponse(response, $"File not found: {modelRequest.Path}");
					return;
				}

				// 7. Load model (on UI thread if needed)
				bool loadSuccess = false;
				if (Application.Current.Dispatcher != null)
				{
					Application.Current.Dispatcher.Invoke(() =>
					{
						loadSuccess = LoadModel(modelRequest.Path);
					});
				}
				else
				{
					loadSuccess = LoadModel(modelRequest.Path);
				}

				// 8. Return appropriate response
				if (loadSuccess)
				{
					WriteResponse(response, JsonConvert.SerializeObject(new
					{
						success = true,
						message = $"Model loaded successfully from: {modelRequest.Path}"
					}));
				}
				else
				{
					response.StatusCode = 500;
					WriteResponse(response, JsonConvert.SerializeObject(new
					{
						success = false,
						message = "Failed to load model"
					}));
				}
			}
			catch (JsonException ex) // Newtonsoft.Json's JsonException
			{
				response.StatusCode = 400;
				WriteResponse(response, JsonConvert.SerializeObject(new
				{
					error = "Invalid JSON format",
					details = ex.Message
				}));
			}
			catch (Exception ex)
			{
				response.StatusCode = 500;
				WriteResponse(response, JsonConvert.SerializeObject(new
				{
					error = "Internal server error",
					details = ex.Message
				}));
			}
			finally
			{
				response.Close();
			}
		}

		private bool LoadModel(string path)
		{
			_mainView.LoadAnyModel(path); // Load the model in the main application	
			return true;							   
		}

		private void HandleShutdownRequest(HttpListenerResponse response)
		{
			StopHttpListener();
			Application.Current.Shutdown(); // Close the application
		}
		private void WriteResponse(HttpListenerResponse response, string content)
		{
			response.ContentType = "application/json";
			byte[] buffer = Encoding.UTF8.GetBytes(content);
			response.ContentLength64 = buffer.Length;
			response.OutputStream.Write(buffer, 0, buffer.Length);
		}
		private void StopHttpListener()
		{
			try
			{
				if (!_isRunning)
				{
					return;
				}
				_isRunning = false; // Stop listening for incoming requests
				_listener?.Stop();
				_listenerThread?.Join(500); // Wait for thread to finish
				_listener?.Close();
			}
			catch (Exception ex) { }
			
		}

	}
}