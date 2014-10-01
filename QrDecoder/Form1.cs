/*
 * Implementation of recognition of the QR CODE. 
 * 
 * Includes:
 *		- recognition of the encrypted message
 *		- detection of the position of the landmark
 * 
 * Uses:
 *		-Zxing library 
 *		-AForge.NET library
 *		
 * 	Matrix transformation is based on AForge.NET sample project http://www.aforgenet.com/articles/posit/
 * 	
 * The QR pattern compatibile with this software, might be generated in any website which offers this functionality.
 * Desired format is c*XXXXX*XXXXXsXXX
 * where:
 *		c - is a preamble
 *		* - is a sign (+/-) of x coordinate
 *		XXXXX - are 5 digits (no comas) (x coordinate)
 *		* - is a sign (+/-) of y coordinate
 *		XXXXX - are 5 digits (no comas) (y coordinate) 
 *		s - is a constant character
 *		XXX - are 3 digits (size of the qr pattern)
 *		
 * For example we want to code coordinates of the laboratory where the wheelchair is (x=7,81; y=-20,64) and the size of the qr mark is 16 cm;
 * The string should be: c+00781-02064s160
 * 
 * 
 * "Always code as if the guy who ends up maintaining your code will be a violent psychopath who knows where you live."
 * 
 * 05-2014 Łukasz Księżak
 */

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using AForge;
using AForge.Imaging;
using AForge.Imaging.Filters;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;
using AForge.Video;
using ZXing.QrCode.Internal;
using Image = System.Drawing.Image;
using AForge;
using AForge.Math;
using AForge.Math.Geometry;

namespace QrDecoder
{
	public partial class Form1 : Form
	{
		private struct Device
		{
			public int Index;
			public string Name;
			public override string ToString()
			{
				return Name;
			}
		}

		private readonly CameraDevices camDevices;
		private Bitmap currentBitmapForDecoding;
		private readonly Thread decodingThread;
		private Result currentResult;
		private readonly Pen resultRectPen;
		private Graphics g;
		List<System.Drawing.Point> pointsToPrint = new List<System.Drawing.Point>();

//Pose estimation variables:

		// image point of the object to estimate pose for
		private AForge.Point[] imagePoints = new AForge.Point[4];
		// model points
		private Vector3[] modelPoints = new Vector3[4];
		// camera's focal length
		private float focalLength;
		// estimated transformation
		private Matrix3x3 rotationMatrix, bestRotationMatrix, alternateRotationMatrix;
		private Vector3 translationVector, bestTranslationVector, alternateTranslationVector;
		private bool isPoseEstimated = false;
		private float modelRadius;
		private System.Drawing.Point p1, p2, p3, p4; //Information about the corners of QR code;
		        private AForge.Point pointPreviousValue;

        // model used to draw coordinate system's axes
        private Vector3[] axesModel = new Vector3[]
        {
            new Vector3( 0, 0, 0 ),
            new Vector3( 1, 0, 0 ),
            new Vector3( 0, 1, 0 ),
            new Vector3( 0, 0, 1 ),
        };

		private int cx;
		private int cy;

        //Determine the location of the camera in relation to the gravity center of the wheelchair.
        private const float X_desplacement = 0f;    
        private const float Y_desplacement = 0.4f;
        
//EndOf PoseEstimation variables;

		public Form1()
		{
			InitializeComponent();
            
			camDevices = new CameraDevices();
			decodingThread = new Thread(DecodeBarcode);
			pictureBox1.Paint += pictureBox1_Paint;
		}

		public string Process(Bitmap bitmap)
		{
			try
			{
				var reader = new BarcodeReader();
				return reader.Decode(bitmap).Text;
			}
			catch
			{
				return "Error";
			}
		}

		public static byte[] ImageToByte(Bitmap img)
		{
			ImageConverter converter = new ImageConverter();
			return (byte[]) converter.ConvertTo(img, typeof (byte[]));
		}

		private void button1_Click(object sender, EventArgs e)	//gives option to read qr mark from the file
		{
			try
			{
				OpenFileDialog open = new OpenFileDialog();
				open.Filter = "Image Files(*.jpg; *.jpeg; *.gif; *.bmp)|*.jpg; *.jpeg; *.gif; *.bmp";
				if (open.ShowDialog() == DialogResult.OK)
				{
					byte[] contenidoArchivo = File.ReadAllBytes(open.FileName);
					MemoryStream stream = new MemoryStream(contenidoArchivo);
					Bitmap image = new Bitmap(Bitmap.FromStream(stream));
					textBox1.Text = Process(image);
				}
				else
				{
					return;
				}
			}
			catch (Exception)
			{
				MessageBox.Show("Couldn't open the file");
			}
		}

		private void button2_Click(object sender, EventArgs e)	//Opens stream from the camera and starts reading thread
		{
			if (comboBox1.SelectedItem != null)
			{
				decodingThread.Start();
				if (camDevices.Current != null)
				{
					camDevices.Current.NewFrame -= Current_NewFrame;
					if (camDevices.Current.IsRunning)
					{
						camDevices.Current.SignalToStop();
					}
				}

				camDevices.SelectCamera(((Device)(comboBox1.SelectedItem)).Index);
				camDevices.Current.NewFrame += Current_NewFrame;
				camDevices.Current.Start();
				
			}
			else
				textBox1.Text = "Select camera!";
		}

		protected override void OnLoad(EventArgs e)
		{
			base.OnLoad(e);
			LoadDevicesToCombobox();
			comboBox1.SelectedIndex = 0;
		}

		protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
		{
			base.OnClosing(e);
			if (!e.Cancel)
			{
				decodingThread.Abort();
				if (camDevices.Current != null)
				{
					camDevices.Current.NewFrame -= Current_NewFrame;
					if (camDevices.Current.IsRunning)
					{
						camDevices.Current.SignalToStop();
					}
				}
			}
		}

		private void LoadDevicesToCombobox()
		{
			comboBox1.Items.Clear();
			for (var index = 0; index < camDevices.Devices.Count; index++)
			{
				comboBox1.Items.Add(new Device { Index = index, Name = camDevices.Devices[index].Name });
			}
		}

		private void Current_NewFrame(object sender, NewFrameEventArgs eventArgs)
		{
			if (IsDisposed)
			{
				return;
			}

			try
			{
				if (currentBitmapForDecoding == null)
				{
					currentBitmapForDecoding = (Bitmap)eventArgs.Frame.Clone();
				}
				Invoke(new Action<Bitmap>(ShowFrame), eventArgs.Frame.Clone());
			}
			catch (ObjectDisposedException ex)
			{
				//textBox1.Text = "Error occured: " + ex.Message;
			}
		}

		private void ShowFrame(Bitmap frame)
		{
			pictureBox1.Image = frame;
		}

		private double rad;
		//Main function of the thread. It is constantly performing detection from the camera's stream. 
		//Detects corners of the qr mark and also prints them on the second picturebox.
		private void DecodeBarcode()
		{
			LuminanceSource source;
			HybridBinarizer binarizer;
			BinaryBitmap binBitmap;
			
			while (true)
			{
				if (currentBitmapForDecoding != null)
				{
					source = new BitmapLuminanceSource(currentBitmapForDecoding);
					binarizer = new HybridBinarizer(source);
					binBitmap = new BinaryBitmap(binarizer);

					BarcodeReader reader = new BarcodeReader()
					{
					TryHarder = true,
					PossibleFormats = new List<BarcodeFormat>
						{
						BarcodeFormat.QR_CODE
						}
					};
					//var result = reader.decode(binBitmap);
					reader.AutoRotate = true;
					reader.TryInverted = true; 

					var result = reader.Decode(currentBitmapForDecoding);
					
					if (result != null)
					{
						button3.Invoke(new Action(() => button3.Enabled = true));
						pictureBox2.Invoke(new Action(() => pictureBox2.BackColor = Color.Green));	//Dedection LED :)
						Invoke(new Action<Result>(ShowResult), result);
						
						int numberOfCorners = result.ResultPoints.Count();

						//The QR Mark has only 3 corners that are being recognized by the algorithm
						//nevertheless we need 4 corners in order to perform co-posit algorithm

						//BE CAREFUL! In the bigger QR Marks (with the content longer than 18 characters) there are normaly 4 corners detected. 
						//When there are only 3 corners detected you need to estimate position of the 4th corner. A bit of simple geometry.

						for (int i = 0; i < numberOfCorners+1; i++) 
						{
							if (i == 3)
							{
								double _x = 0;
								double _y = 0;

								double arc =
									Math.Atan((-result.ResultPoints[1].Y + result.ResultPoints[0].Y)/
									          (result.ResultPoints[1].X - result.ResultPoints[0].X));

								double side =
									Math.Sqrt(Math.Pow(result.ResultPoints[1].X - result.ResultPoints[0].X, 2) +
											  Math.Pow(result.ResultPoints[1].Y - result.ResultPoints[0].Y, 2));

								rad = Math.Atan2(result.ResultPoints[1].X - result.ResultPoints[0].X,
								           -result.ResultPoints[1].Y + result.ResultPoints[0].Y)*180/Math.PI + 180;

								if (rad < 180)
								{
									_x = result.ResultPoints[0].X - side*Math.Sin(arc);
									_y = result.ResultPoints[0].Y - side*Math.Cos(arc);
								}
								else
								{
									_x = result.ResultPoints[0].X + side * Math.Sin(arc);
									_y = result.ResultPoints[0].Y + side * Math.Cos(arc);
								}
								System.Drawing.Point a = new System.Drawing.Point((int)(_x), (int)(_y));
							
								pointsToPrint.Add(a);
							}
							else
							{
								pointsToPrint.Add(new System.Drawing.Point((int)result.ResultPoints[i].X, (int)result.ResultPoints[i].Y));
							}
						Invoke(new Action(PrintPoints));
						}

					}
					else //result==null
					{
						button3.Invoke(new Action(() => button3.Enabled = false));
						pictureBox2.Invoke(new Action(() => pictureBox2.BackColor = Color.Red));
						textBox1.Invoke(new Action (() => textBox1.Text =""));
					}
					currentBitmapForDecoding.Dispose();
					currentBitmapForDecoding = null;
				}
				Thread.Sleep(10);
			}
		}

		private void PrintPoints()
		{
			Image img = pictureBox1.Image;

			Graphics g = Graphics.FromImage(img);

			if (pointsToPrint.Count == 4)
			{
				p1 = pointsToPrint[0];
				p2 = pointsToPrint[1];
				p3 = pointsToPrint[2];
				p4 = pointsToPrint[3];
				
				//double width =
				//	Math.Sqrt((Math.Pow((double)(pointsToPrint[2].X - pointsToPrint[1].X),2.0)+ (Math.Pow((double)(pointsToPrint[2].Y - pointsToPrint[1].Y),2.0) )));
				//double height =
				//	Math.Sqrt((Math.Pow((double)(pointsToPrint[0].X - pointsToPrint[1].X), 2.0) + (Math.Pow((double)(pointsToPrint[0].Y - pointsToPrint[1].Y), 2.0))));

				//g.DrawRectangle(new System.Drawing.Pen(Color.Blue, 25.0f), pointsToPrint[1].X, pointsToPrint[1].Y, (float) width,
				//				(float) height);
				//pictureBox1.Refresh();
				pointsToPrint.Clear();
			}

			g.DrawImage(img, new System.Drawing.Point(0, 0));
			pictureBox1.Refresh();
		}

		private void ShowResult(Result result)
		{
			currentResult = result;
			textBox1.Text = result.Text + "\n";
		}

		public static Image Convert(Bitmap oldbmp)
		{
			using (var ms = new MemoryStream())
			{
				oldbmp.Save(ms, ImageFormat.Jpeg);
				ms.Position = 0;
				return Image.FromStream(ms);
			}
		}

		private void pictureBox1_Paint(object sender, PaintEventArgs e){}

		//Performing Co-Poist algorithm on the detected QR mark. 
		//Very important factor which needs to be set is FOCAL LENGTH!

		private void button3_Click(object sender, EventArgs e)
		{

            
			pictureBox3.Image = pictureBox1.Image;
			pictureBox3.Size = pictureBox3.Image.Size;

			cx = (pictureBox3.Width / 2);
			cy = (pictureBox3.Height / 2);
			
			//Good for tests: printing rectangle when QRMARK is found
			//Graphics d = Graphics.FromImage(pictureBox3.Image);
			//	double width =
			//		Math.Sqrt((Math.Pow((double)(p3.X - p1.X), 2.0) + (Math.Pow((double)(p3.Y - p2.Y), 2.0))));
			//	double height =
			//		Math.Sqrt((Math.Pow((double)(p1.X - p2.X), 2.0) + (Math.Pow((double)(p1.Y - p2.Y), 2.0))));

			//	d.DrawRectangle(new System.Drawing.Pen(Color.Blue, 25.0f), p2.X, p2.Y, (float)width,
			//					(float)height);
			//	pictureBox3.Refresh();

			//	d.DrawImage(pictureBox3.Image, new System.Drawing.Point(0, 0));
			//	pictureBox3.Refresh();

		    ExtractDataFromQRMessage();

			focalLength = 640;
			GetCoordinateValue();
			EstimatePose();
			
			UpdateEstimationInformation();

			Calculate_V3();
		}

		private void ExtractDataFromQRMessage()
		{
			//there is fixed number of data that should be stored in QR MARK (c+34567+90123S567)	
			string numberX = "0", numberY = "0", numberSize = "0";
			
			if(textBox1.Text[0] == 'c') //preambule
			{
				if(textBox1.Text[1] == '+')
				{
					numberX = string.Concat(textBox1.Text[2], textBox1.Text[3], textBox1.Text[4], textBox1.Text[5], textBox1.Text[6]);
				}
				else if(textBox1.Text[1] == '-')
				{
					numberX = string.Concat("-",textBox1.Text[2], textBox1.Text[3], textBox1.Text[4], textBox1.Text[5], textBox1.Text[6]);
				}
				if (textBox1.Text[7] == '+')
				{
					numberY = string.Concat(textBox1.Text[8], textBox1.Text[9], textBox1.Text[10], textBox1.Text[11], textBox1.Text[12]);
				}
				else if (textBox1.Text[7] == '-')
				{
					numberY = string.Concat(textBox1.Text[8], textBox1.Text[9], textBox1.Text[10], textBox1.Text[11], textBox1.Text[12]);
				}
				if(textBox1.Text[13]== 's')
				{
					numberSize = string.Concat(textBox1.Text[14], textBox1.Text[15], textBox1.Text[16]);
				}
				lblXnode.Text = numberX;
				lblYnode.Text = numberY;
				lblSize.Text = numberSize;

			}
		}

		private void ClearEstimation()
		{
			isPoseEstimated = false;
		}

		private AForge.Point[] PerformProjection(Vector3[] model, Matrix4x4 transformationMatrix, int viewSize)
		{
			AForge.Point[] projectedPoints = new AForge.Point[model.Length];

			for (int i = 0; i < model.Length; i++)
			{
				Vector3 scenePoint = (transformationMatrix * model[i].ToVector4()).ToVector3();

				projectedPoints[i] = new AForge.Point(
					(int)(scenePoint.X / scenePoint.Z * viewSize),
					(int)(scenePoint.Y / scenePoint.Z * viewSize));
			}

			return projectedPoints;
		}

		private void pictureBox3_Paint(object sender, PaintEventArgs e)
		{
			Graphics g = e.Graphics;

			if (pictureBox3.Image != null)
			{
				if (isPoseEstimated)
				{
					AForge.Point[] projectedAxes = PerformProjection(axesModel,
						// create tranformation matrix
						Matrix4x4.CreateTranslation(translationVector) *       // 3: translate
						Matrix4x4.CreateFromRotation(rotationMatrix) *         // 2: rotate
						Matrix4x4.CreateDiagonal(
							new Vector4(modelRadius, modelRadius, modelRadius, 1)), // 1: scale
						pictureBox3.Width
					);

					using (Pen pen = new Pen(Color.Blue, 5))
					{
						g.DrawLine(pen,
							cx + projectedAxes[0].X, cy - projectedAxes[0].Y,
							cx + projectedAxes[1].X, cy - projectedAxes[1].Y);
					}

					using (Pen pen = new Pen(Color.Red, 5))
					{
						g.DrawLine(pen,
							cx + projectedAxes[0].X, cy - projectedAxes[0].Y,
							cx + projectedAxes[2].X, cy - projectedAxes[2].Y);
					}

					using (Pen pen = new Pen(Color.Lime, 5))
					{
						g.DrawLine(pen,
							cx + projectedAxes[0].X, cy - projectedAxes[0].Y,
							cx + projectedAxes[3].X, cy - projectedAxes[3].Y);
					}
				}
			}
		}//EndOf pictureBox3_Paint event
		
		private void GetCoordinateValue()
		{
			//The assumption is that the QRMarker's border is 160mm and the center of coordinates' system is in the QRMarker's center
			//TODO: should be proportional to the variable 'size' coded in the qrmark (lack of the time)
 
			modelPoints[0].X = -80f;	//left bottom
			modelPoints[0].Y = -80;
			modelPoints[0].Z = 0f;
			modelPoints[1].X = -80f;	//left top
			modelPoints[1].Y = 80;
			modelPoints[1].Z = 0f;
			modelPoints[2].X = 80f;	//right top
			modelPoints[2].Y = 80;
			modelPoints[2].Z = 0f;
			modelPoints[3].X = 80f;	//right bottom
			modelPoints[3].Y = -80;
			modelPoints[3].Z = 0f;

			imagePoints[0] = new AForge.Point((p1.X - cx), (cy - p1.Y));
			imagePoints[1] = new AForge.Point((p2.X - cx), (cy - p2.Y));
			imagePoints[2] = new AForge.Point((p3.X - cx), (cy - p3.Y));
			imagePoints[3] = new AForge.Point((p4.X - cx), (cy - p4.Y));

			using (Graphics d = Graphics.FromImage(pictureBox3.Image))
			{
				Rectangle rect = new Rectangle(cx + (int)imagePoints[0].X - 15, cy - (int)imagePoints[0].Y-15, 25, 25);
				d.FillEllipse(new SolidBrush(Color.Red), rect);
				rect = new Rectangle(cx + (int)imagePoints[1].X-15, cy - (int)imagePoints[1].Y-15, 25, 25);
				d.FillEllipse(new SolidBrush(Color.Blue), rect);
				rect = new Rectangle(cx + (int)imagePoints[2].X-15, cy - (int)imagePoints[2].Y-15, 25, 25);
				d.FillEllipse(new SolidBrush(Color.Yellow), rect);
				rect = new Rectangle(cx + (int)imagePoints[3].X-15, cy - (int)imagePoints[3].Y-15, 25, 25);
				d.FillEllipse(new SolidBrush(Color.Green), rect);

				pictureBox3.Refresh();

				d.DrawImage(pictureBox3.Image, new System.Drawing.Point(0, 0));
				pictureBox3.Refresh();
			}
		}

		// Estimate 3D position
		private void EstimatePose()
		{
			try
			{
				// calculate model's center
				Vector3 modelCenter = new Vector3(
					(modelPoints[0].X + modelPoints[1].X + modelPoints[2].X + modelPoints[3].X) / 4,
					(modelPoints[0].Y + modelPoints[1].Y + modelPoints[2].Y + modelPoints[3].Y) / 4,
					(modelPoints[0].Z + modelPoints[1].Z + modelPoints[2].Z + modelPoints[3].Z) / 4
				);

				// calculate ~ model's radius
				modelRadius = 0;
				foreach (Vector3 modelPoint in modelPoints)
				{
					float distanceToCenter = (modelPoint - modelCenter).Norm;
					if (distanceToCenter > modelRadius)
					{
						modelRadius = distanceToCenter;
					}
				}

				CoplanarPosit coposit = new CoplanarPosit(modelPoints, focalLength);
				coposit.EstimatePose(imagePoints, out rotationMatrix, out translationVector);

				bestRotationMatrix = coposit.BestEstimatedRotation;
				bestTranslationVector = coposit.BestEstimatedTranslation;

				alternateRotationMatrix = coposit.AlternateEstimatedRotation;
				alternateTranslationVector = coposit.AlternateEstimatedTranslation;

				isPoseEstimated = true;
				//UpdateEstimationInformation();
				pictureBox3.Invalidate();
			}
			catch (ApplicationException ex)
			{
				MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
			}
		}

		private void UpdateEstimationInformation()
		{
			estimatedTransformationMatrixControl.SetMatrix(
				Matrix4x4.CreateTranslation(translationVector) *
				Matrix4x4.CreateFromRotation(rotationMatrix));

			float estimatedYaw;
			float estimatedPitch;
			float estimatedRoll;

			rotationMatrix.ExtractYawPitchRoll(out estimatedYaw, out estimatedPitch, out estimatedRoll);

			estimatedYaw *= (float)(180.0 / Math.PI);
			estimatedPitch *= (float)(180.0 / Math.PI);
			estimatedRoll *= (float)(180.0 / Math.PI);

			lblYaw.Text = estimatedYaw.ToString();
			lblPitch.Text = estimatedPitch.ToString();
			lblRoll.Text = estimatedRoll.ToString();
		}

		//code which might be used to give points coordinates manualy (as it was in the demo from the website);
		//private int temp = 0;
		private void pictureBox3_MouseClick(object sender, MouseEventArgs e)	
		{
			//textBox2.AppendText("\n MouseX: " + (e.X - pictureBox3.Width / 2) + "; Y: " + (pictureBox3.Height / 2 - e.Y) + ";\n");
			
			//imagePoints[temp] = new AForge.Point((e.X - pictureBox3.Width/2), (pictureBox3.Height/2 - e.Y));
			//temp++;
			//if (temp==4)
			//{
			//	focalLength = 400;
			//	GetCoordinateValue();
			//	EstimatePose();
			//}
		}

		private void Form1_Load(object sender, EventArgs e)
		{
			WindowState = FormWindowState.Maximized;
            lbl_desplace_X.Text = X_desplacement.ToString();
            lbl_desplace_Y.Text = Y_desplacement.ToString();
		}

		private void Calculate_V3()
		{

			float X_V1 = float.Parse(lblXnode.Text) / 100;
			if (textBox1.Text[1] == '-') X_V1 = -X_V1;
			float Y_V1 = float.Parse(lblYnode.Text) / 100;
			if (textBox1.Text[7] == '-') Y_V1 = -Y_V1;
			float Z_V1 = 0;

			float X_V2 = translationVector.X/1000 - X_desplacement;
			float Y_V2 = translationVector.Y/1000 + Y_desplacement;
			float Z_V2 = translationVector.Z/1000;

			float alfa, beta, gamma;

			bestRotationMatrix.ExtractYawPitchRoll(out alfa, out beta, out gamma);
			//rotationMatrix.ExtractYawPitchRoll(out alfa, out beta, out gamma);

			alfa = (float)(Math.PI);
			beta = 0;
			gamma *= (float)(180.0 / Math.PI);

			Vector3 R = new Vector3(alfa, beta, gamma);

			Vector3 V1 = new Vector3(X_V1,Y_V1,Z_V1);
			Vector3 V2 = new Vector3(X_V2,Y_V2,Z_V2);
			Vector3 V3 = new Vector3();

			//Calculation based on article: 
			//Fusing odometric and vision data with an EKF to estimate the absolute position of an autonomous mobile robot
			//M. Marrón J.C. García M.A. Sotelo E. López M. Mazo

			V3.X = V2.X + V1.X*(float) Math.Cos(V1.Z) - V1.Y*(float) Math.Sin(V1.Z);
			V3.Y = V2.Y + V1.X*(float) Math.Sin(V1.Z) + V1.Y*(float) Math.Cos(V1.Z);
			V3.Z = gamma;

			V3_X.Text = V3.X.ToString();
			V3_Y.Text = V3.Y.ToString();
			V3_Z.Text = V3.Z.ToString();
		}
	}
}
