using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Devices.Sensors;
using Windows.Graphics.Display;
using Windows.Graphics.Imaging;
using Windows.Media;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage.FileProperties;
using Windows.System.Display;
using Windows.System.Profile;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;

// The Blank Page item template is documented at https://go.microsoft.com/fwlink/?LinkId=402352&clcid=0x409

namespace QR
{
	/// <summary>
	/// An empty page that can be used on its own or navigated to within a Frame.
	/// </summary>
	public sealed partial class MainPage : Page
    {
		[ComImport]
		[Guid("5b0d3235-4dba-4d44-865e-8f1d0e4fd04d")]
		[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]

		unsafe interface IMemoryBufferByteAccess
		{
			void GetBuffer(out byte* buffer, out uint capacity);
		}

		// Receive notifications about rotation of the UI and apply any necessary rotation to the preview stream
		private readonly DisplayInformation _displayInformation = DisplayInformation.GetForCurrentView();
		private DisplayOrientations _displayOrientation = DisplayOrientations.Portrait;

		// Rotation metadata to apply to the preview stream (MF_MT_VIDEO_ROTATION)
		// Reference: http://msdn.microsoft.com/en-us/library/windows/apps/xaml/hh868174.aspx
		private static readonly Guid RotationKey = new Guid("C380465D-2271-428C-9B83-ECEA3B4A85C1");

		// Prevent the screen from sleeping while the camera is running
		private readonly DisplayRequest _displayRequest = new DisplayRequest();

		// For listening to media property changes
		private readonly SystemMediaTransportControls _systemMediaControls = SystemMediaTransportControls.GetForCurrentView();

		// MediaCapture and its state variables
		private MediaCapture _mediaCapture;
		private bool _isInitialized = false;
		private bool _isPreviewing = false;

		private DispatcherTimer CaptureTimer;
		private DispatcherTimer FocusTimer;

		protected override async void OnNavigatedTo(NavigationEventArgs e)
		{
			// Populate orientation variables with the current state and register for future changes
			_displayOrientation = _displayInformation.CurrentOrientation;
			_displayInformation.OrientationChanged += _displayInformation_OrientationChanged;

			await InitializeCameraAsync();

			CaptureTimer = new DispatcherTimer();
			CaptureTimer.Interval = TimeSpan.FromMilliseconds(300);
			CaptureTimer.Tick += async (sender, ev) =>
			{
				await FindQrCodeFromVideoFrame(); // capture a frame and find QR code
				};

			FocusTimer = new DispatcherTimer();
			FocusTimer.Interval = TimeSpan.FromMilliseconds(2000);
			FocusTimer.Tick += (sender, ev) =>
			{
				TryToFocus();
			};

			CaptureTimer.Start();
			FocusTimer.Start();

			await SetPreviewRotationAsync();
		}

		private async void _displayInformation_OrientationChanged(DisplayInformation sender, object args)
		{
			await SetPreviewRotationAsync();
		}

		private const string WINDOWS_DESKTOP = "Windows.Desktop";

		private async Task SetPreviewRotationAsync()
		{
			if (_mediaCapture == null) { return; }

			// Populate orientation variables with the current state
			_displayOrientation = _displayInformation.CurrentOrientation;

			// Calculate which way and how far to rotate the preview
			int rotationDegrees = ConvertDisplayOrientationToDegrees(_displayOrientation);

			// On Desktop, this setting may stop some of webcams.
			if (AnalyticsInfo.VersionInfo.DeviceFamily != WINDOWS_DESKTOP)
			{
				var props = _mediaCapture.VideoDeviceController.GetMediaStreamProperties(MediaStreamType.VideoPreview);
				props.Properties.Add(RotationKey, rotationDegrees);
				await _mediaCapture.SetEncodingPropertiesAsync(MediaStreamType.VideoPreview, props, null);
			}
		}

		private static PhotoOrientation ConvertOrientationToPhotoOrientation(SimpleOrientation orientation)
		{
			switch (orientation)
			{
				case SimpleOrientation.Rotated90DegreesCounterclockwise:
					return PhotoOrientation.Rotate90;
				case SimpleOrientation.Rotated180DegreesCounterclockwise:
					return PhotoOrientation.Rotate180;
				case SimpleOrientation.Rotated270DegreesCounterclockwise:
					return PhotoOrientation.Rotate270;
				case SimpleOrientation.NotRotated:
				default:
					return PhotoOrientation.Normal;
			}
		}

		private static int ConvertDisplayOrientationToDegrees(DisplayOrientations orientation)
		{
			switch (orientation)
			{
				case DisplayOrientations.Portrait:
					return 90;
				case DisplayOrientations.LandscapeFlipped:
					return 180;
				case DisplayOrientations.PortraitFlipped:
					return 270;
				case DisplayOrientations.Landscape:
				default:
					return 0;
			}
		}

		protected override async void OnNavigatingFrom(NavigatingCancelEventArgs e)
		{
			_displayInformation.OrientationChanged -= _displayInformation_OrientationChanged;

			CaptureTimer.Stop();
			FocusTimer.Stop();
			await CleanupCameraAsync();
		}

		async void TryToFocus()
		{
			if (_mediaCapture == null) { return; }

			var focusControl = _mediaCapture.VideoDeviceController.FocusControl;
			if (focusControl.Supported)
			{
				await focusControl.FocusAsync();
			}
		}

		/// <summary>
		/// In the event of the app being minimized this method handles media property change events. If the app receives a mute
		/// notification, it is no longer in the foreground.
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="args"></param>
		private async void SystemMediaControls_PropertyChanged(SystemMediaTransportControls sender, SystemMediaTransportControlsPropertyChangedEventArgs args)
		{
			await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
			{
					// Only handle this event if this page is currently being displayed
					if (args.Property == SystemMediaTransportControlsProperty.SoundLevel && Frame.CurrentSourcePageType == typeof(MainPage))
				{
						// Check to see if the app is being muted. If so, it is being minimized.
						// Otherwise if it is not initialized, it is being brought into focus.
						if (sender.SoundLevel == SoundLevel.Muted)
					{
						await CleanupCameraAsync();
					}
					else if (!_isInitialized)
					{
						await InitializeCameraAsync();
					}
				}
			});
		}

		private async void MediaCapture_Failed(MediaCapture sender, MediaCaptureFailedEventArgs errorEventArgs)
		{
			Debug.WriteLine("MediaCapture_Failed: (0x{0:X}) {1}", errorEventArgs.Code, errorEventArgs.Message);

			await CleanupCameraAsync();
		}


		/// <summary>
		/// Initializes the MediaCapture, registers events, gets camera device information for mirroring and rotating, and starts preview
		/// </summary>
		/// <returns></returns>
		private async Task InitializeCameraAsync()
		{
			Debug.WriteLine("InitializeCameraAsync");

			if (_mediaCapture == null)
			{
				// Attempt to get the back camera if one is available, but use any camera device if not
				var cameraDevice = await FindCameraDeviceByPanelAsync(Windows.Devices.Enumeration.Panel.Back);

				if (cameraDevice == null)
				{
					Debug.WriteLine("No camera device found!");
					return;
				}

				// Create MediaCapture and its settings
				_mediaCapture = new MediaCapture();

				// Register for a notification when something goes wrong
				_mediaCapture.Failed += MediaCapture_Failed;

				var settings = new MediaCaptureInitializationSettings
				{
					VideoDeviceId = cameraDevice.Id,
					StreamingCaptureMode = StreamingCaptureMode.Video,
				};

				// Initialize MediaCapture
				try
				{
					await _mediaCapture.InitializeAsync(settings);
					_isInitialized = true;
				}
				catch (UnauthorizedAccessException)
				{
					Debug.WriteLine("The app was denied access to the camera");
				}

				// If initialization succeeded, start the preview
				if (_isInitialized)
				{
					await StartPreviewAsync();
				}
			}
		}

		/// <summary>
		/// Starts the preview and adjusts it for rotation and mirroring after making a request to keep the screen on and unlocks the UI
		/// </summary>
		/// <returns></returns>
		private async Task StartPreviewAsync()
		{
			Debug.WriteLine("StartPreviewAsync");

			await Task.Delay(TimeSpan.FromMilliseconds(800));

			// Prevent the device from sleeping while the preview is running
			_displayRequest.RequestActive();

			// Register to listen for media property changes
			_systemMediaControls.PropertyChanged += SystemMediaControls_PropertyChanged;

			// Set the preview source in the UI and mirror it if necessary
			PreviewElement.Source = _mediaCapture;
			PreviewElement.FlowDirection = FlowDirection.LeftToRight;

			// Start the preview
			await _mediaCapture.StartPreviewAsync();

			_isPreviewing = true;
		}

		/// <summary>
		/// Stops the preview and deactivates a display request, to allow the screen to go into power saving modes, and locks the UI
		/// </summary>
		/// <returns></returns>
		private async Task StopPreviewAsync()
		{
			_isPreviewing = false;
			if (_mediaCapture == null) { return; }

			await _mediaCapture.StopPreviewAsync();

			// Use the dispatcher because this method is sometimes called from non-UI threads
			await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
			{
				PreviewElement.Source = null;

					// Allow the device to sleep now that the preview is stopped
					_displayRequest.RequestRelease();

			});
		}

		private async Task FindQrCodeFromVideoFrame()
		{
			if (_mediaCapture == null) { return; }

			// Get information about the preview
			var previewProperties = _mediaCapture.VideoDeviceController.GetMediaStreamProperties(MediaStreamType.VideoPreview) as VideoEncodingProperties;

			// Create the video frame to request a SoftwareBitmap preview frame
			var videoFrame = new VideoFrame(BitmapPixelFormat.Bgra8, (int)previewProperties.Width, (int)previewProperties.Height);

			// Capture the preview frame
			VideoFrame currentFrame = null;

			try
			{
				using (currentFrame = await _mediaCapture.GetPreviewFrameAsync())
				{
					// Collect the resulting frame
					var FoundString = Decode(currentFrame.SoftwareBitmap);
					if (FoundString != null)
					{
						Readed.Text += Environment.NewLine;
						Readed.Text += FoundString;
					}
				}
			}
			catch (Exception e)
			{
				Debug.WriteLine("Caught exception during reading current frame. Maybe device is busy or not initialized yet ...");
				Debug.WriteLine(e.Message);
			}

		}

		BarcodeReader barcodeReader = new BarcodeReader();
		QRCodeReader qrCodeReader = new QRCodeReader();

		public string Decode(SoftwareBitmap image)
		{
			var bitmap = new BinaryBitmap(new HybridBinarizer(new RGBLuminanceSource();
			image.CopyToBuffer(bitmap.PixelBuffer);

			var result = qrCodeReader.decode(bitmap);
			if (result != null)
			{
				return result.Text;
			}
			return null;
		}

		/// <summary>
		/// Cleans up the camera resources (after stopping the preview if necessary) and unregisters from MediaCapture events
		/// </summary>
		/// <returns></returns>
		private async Task CleanupCameraAsync()
		{
			if (_isInitialized)
			{
				if (_isPreviewing)
				{
					// The call to stop the preview is included here for completeness, but can be
					// safely removed if a call to MediaCapture.Dispose() is being made later,
					// as the preview will be automatically stopped at that point
					await StopPreviewAsync();
				}

				_isInitialized = false;
			}

			if (_mediaCapture != null)
			{
				_mediaCapture.Failed -= MediaCapture_Failed;
				_mediaCapture.Dispose();
				_mediaCapture = null;
			}
		}

		/// <summary>
		/// Queries the available video capture devices to try and find one mounted on the desired panel
		/// </summary>
		/// <param name="desiredPanel">The panel on the device that the desired camera is mounted on</param>
		/// <returns>A DeviceInformation instance with a reference to the camera mounted on the desired panel if available,
		///          any other camera if not, or null if no camera is available.</returns>
		private static async Task<DeviceInformation> FindCameraDeviceByPanelAsync(Windows.Devices.Enumeration.Panel desiredPanel)
		{
			// Get available devices for capturing pictures
			var allVideoDevices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);

			// Get the desired camera by panel
			DeviceInformation desiredDevice = allVideoDevices.FirstOrDefault(x => x.EnclosureLocation != null && x.EnclosureLocation.Panel == desiredPanel);

			// If there is no device mounted on the desired panel, return the first device found
			return desiredDevice ?? allVideoDevices.FirstOrDefault();
		}

		public MainPage()
		{
			this.InitializeComponent();
		}
    }
}
