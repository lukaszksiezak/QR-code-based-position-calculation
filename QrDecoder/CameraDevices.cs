using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AForge.Video.DirectShow;

namespace QrDecoder
{
	class CameraDevices
	{
		public FilterInfoCollection Devices { get; private set; }
		public VideoCaptureDevice Current { get; private set; }
		
		public CameraDevices()
		{
			Devices = new FilterInfoCollection(FilterCategory.VideoInputDevice);
		}

		public void SelectCamera(int index)
		{
			if (index >= Devices.Count)
			{
				throw new ArgumentOutOfRangeException("index");
			}
        Current = new VideoCaptureDevice(Devices[index].MonikerString);
		}
	}
}

	
