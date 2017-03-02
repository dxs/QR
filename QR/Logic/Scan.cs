using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Media.Capture;
using Windows.Storage;
using ZXing;

namespace QR.Logic
{
	public class Scan
	{
		IBarcodeReader reader = new BarcodeReader();

		public Scan()
		{

		}

		public async void TakePicture()
		{
			CameraCaptureUI a = new CameraCaptureUI();
			

		}
	}
}
