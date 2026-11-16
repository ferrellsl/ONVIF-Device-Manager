// Minimal client for the ONVIF Media2 service (ver20/media/wsdl): just the three calls the
// Video streaming page needs to show and change H.265 encoder settings. Media1 (which the
// rest of ODM uses) has no way to express H.265, so an H.265 camera reports H264 there.
//
// The calls go through WCF (so ODM's WS-Security header and binding are reused) but the
// bodies are built and parsed with XLinq rather than generated types: cameras are loose
// with Media2 namespaces and attributes, and SetVideoEncoderConfiguration sends back the
// camera's own XML with only the edited fields changed, so nothing it returned is lost.

namespace onvif.services {
	using System;
	using System.Collections.Generic;
	using System.Globalization;
	using System.Linq;
	using System.ServiceModel;
	using System.ServiceModel.Channels;
	using System.Xml;
	using System.Xml.Linq;
	using WcfMessage = System.ServiceModel.Channels.Message;

	[ServiceContract(Namespace = Media2Client.Tr2)]
	public interface IMedia2Raw {
		[OperationContract(Action = "*", ReplyAction = "*")]
		WcfMessage Invoke(WcfMessage request);
	}

	public class Media2VideoEncoderOptions {
		public string encodingName;
		public VideoEncoding? encoding;
		public float qualityMin = -1, qualityMax = -1;
		public VideoResolution[] resolutions = new VideoResolution[0];
		public IntRange bitrateRange;
		public IntRange govLengthRange;
		public float[] frameRates = new float[0];
		public string[] profiles = new string[0];
	}

	public class Media2VideoEncoderConfiguration {
		/// <summary>The camera's own XML for this configuration.</summary>
		public XElement xml;
		public string token;
		public string encodingName;
		public VideoEncoding? encoding;
		public VideoResolution resolution;
		public float quality = -1;
		public float frameRateLimit = -1;
		public int bitrateLimit = -1;
		public int govLength = -1;
		public string profile;
	}

	public class Media2Client {
		public const string Tr2 = "http://www.onvif.org/ver20/media/wsdl";
		public const string Tt = "http://www.onvif.org/ver10/schema";
		static readonly XNamespace tr2 = Tr2;
		static readonly XNamespace tt = Tt;

		readonly IMedia2Raw channel;
		readonly MessageVersion version;

		public Media2Client(IMedia2Raw channel, MessageVersion version) {
			this.channel = channel;
			this.version = version;
		}

		/// <summary>Appends to %LOCALAPPDATA%\Onvif Device Manager\logs\media2.log (bodies only: no credentials).</summary>
		public static void Log(string text) {
			try {
				var dir = System.IO.Path.Combine(
					Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Onvif Device Manager", "logs");
				System.IO.Directory.CreateDirectory(dir);
				var path = System.IO.Path.Combine(dir, "media2.log");
				if (System.IO.File.Exists(path) && new System.IO.FileInfo(path).Length > 1048576) {
					System.IO.File.Delete(path);
				}
				System.IO.File.AppendAllText(path, System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + text + "\r\n");
			} catch {
			}
		}

		static string Clip(string s) {
			return s.Length > 6000 ? s.Substring(0, 6000) + " ..." : s;
		}

		/// <summary>The last reply body, for logging when ODM can't use what the camera sent.</summary>
		public string LastReply { get; private set; }

		XElement Call(string operation, XElement body) {
			var request = WcfMessage.CreateMessage(
				version, Tr2 + "/" + operation, XmlDictionaryReader.CreateDictionaryReader(body.CreateReader()));
			var requestText = Clip(body.ToString(SaveOptions.DisableFormatting));
			LastReply = null;
			try {
				using (var reply = channel.Invoke(request)) {
					if (reply.IsFault) {
						var fault = MessageFault.CreateFault(reply, 64 * 1024);
						Log(operation + " fault: " + (fault.Reason != null ? fault.Reason.ToString() : "") +
							(fault.Code != null && fault.Code.SubCode != null ? " (" + fault.Code.SubCode.Name + ")" : "") +
							"; request: " + requestText);
						throw new FaultException(fault, reply.Headers.Action);
					}
					using (var reader = reply.GetReaderAtBodyContents()) {
						reader.MoveToContent();
						var resp = (XElement)XNode.ReadFrom(reader);
						LastReply = Clip(resp.ToString(SaveOptions.DisableFormatting));
						return resp;
					}
				}
			} catch (FaultException) {
				throw;
			} catch (Exception err) {
				Log(operation + " failed: " + err.GetType().Name + ": " + err.Message + "; request: " + requestText);
				throw;
			}
		}

		// --- parsing helpers (match by local name: cameras don't always use tt:) ---

		static IEnumerable<XElement> Kids(XElement e, string name) {
			return e == null ? Enumerable.Empty<XElement>() : e.Elements().Where(x => x.Name.LocalName == name);
		}
		static XElement Kid(XElement e, string name) {
			return Kids(e, name).FirstOrDefault();
		}
		static string Attr(XElement e, string name) {
			if (e == null) return null;
			var a = e.Attributes().FirstOrDefault(x => x.Name.LocalName == name);
			return a == null ? null : a.Value;
		}
		static float ParseFloat(string s, float def) {
			float v;
			return s != null && Single.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : def;
		}
		static int ParseInt(string s, int def) {
			if (s == null) return def;
			int v;
			if (Int32.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return v;
			var f = ParseFloat(s, Single.NaN);
			return Single.IsNaN(f) ? def : (int)Math.Round(f);
		}
		static string[] Words(string s) {
			return s == null ? new string[0] : s.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
		}
		static VideoResolution ParseResolution(XElement e) {
			if (e == null) return null;
			return new VideoResolution() {
				width = ParseInt((string)Kid(e, "Width"), 0),
				height = ParseInt((string)Kid(e, "Height"), 0)
			};
		}
		static IntRange ParseIntRange(XElement e) {
			if (e == null) return null;
			return new IntRange() { min = ParseInt((string)Kid(e, "Min"), 0), max = ParseInt((string)Kid(e, "Max"), 0) };
		}

		/// <summary>Media2 encoding name (tt:VideoEncodingMimeNames) to ODM's enum.</summary>
		public static VideoEncoding? ToEncoding(string name) {
			switch ((name ?? "").Trim().ToUpperInvariant()) {
				case "H264": return VideoEncoding.h264;
				case "H265": return VideoEncoding.h265;
				case "JPEG": return VideoEncoding.jpeg;
				case "MPV4-ES":
				case "MPEG4": return VideoEncoding.mpeg4;
				default: return null;
			}
		}

		static Media2VideoEncoderConfiguration ParseConfiguration(XElement c) {
			var rate = Kid(c, "RateControl");
			var encodingName = ((string)Kid(c, "Encoding") ?? "").Trim();
			return new Media2VideoEncoderConfiguration() {
				xml = c,
				token = Attr(c, "token"),
				encodingName = encodingName,
				encoding = ToEncoding(encodingName),
				resolution = ParseResolution(Kid(c, "Resolution")),
				quality = ParseFloat((string)Kid(c, "Quality"), -1),
				frameRateLimit = rate == null ? -1 : ParseFloat((string)Kid(rate, "FrameRateLimit"), -1),
				bitrateLimit = rate == null ? -1 : ParseInt((string)Kid(rate, "BitrateLimit"), -1),
				govLength = ParseInt(Attr(c, "GovLength"), -1),
				profile = Attr(c, "Profile")
			};
		}

		// --- the three calls ---

		public Media2VideoEncoderConfiguration[] GetVideoEncoderConfigurations(string configurationToken, string profileToken) {
			var body = new XElement(tr2 + "GetVideoEncoderConfigurations");
			if (!String.IsNullOrEmpty(configurationToken)) body.Add(new XElement(tr2 + "ConfigurationToken", configurationToken));
			if (!String.IsNullOrEmpty(profileToken)) body.Add(new XElement(tr2 + "ProfileToken", profileToken));
			var resp = Call("GetVideoEncoderConfigurations", body);
			return Kids(resp, "Configurations").Select(ParseConfiguration).ToArray();
		}

		public Media2VideoEncoderOptions[] GetVideoEncoderConfigurationOptions(string configurationToken, string profileToken) {
			var body = new XElement(tr2 + "GetVideoEncoderConfigurationOptions");
			if (!String.IsNullOrEmpty(configurationToken)) body.Add(new XElement(tr2 + "ConfigurationToken", configurationToken));
			if (!String.IsNullOrEmpty(profileToken)) body.Add(new XElement(tr2 + "ProfileToken", profileToken));
			var resp = Call("GetVideoEncoderConfigurationOptions", body);
			return Kids(resp, "Options").Select(o => {
				var name = ((string)Kid(o, "Encoding") ?? "").Trim();
				var quality = Kid(o, "QualityRange");
				IntRange gov = null;
				var govWords = Words(Attr(o, "GovLengthRange"));
				if (govWords.Length >= 2) {
					gov = new IntRange() { min = ParseInt(govWords[0], 0), max = ParseInt(govWords[govWords.Length - 1], 0) };
				}
				return new Media2VideoEncoderOptions() {
					encodingName = name,
					encoding = ToEncoding(name),
					qualityMin = quality == null ? -1 : ParseFloat((string)Kid(quality, "Min"), -1),
					qualityMax = quality == null ? -1 : ParseFloat((string)Kid(quality, "Max"), -1),
					resolutions = Kids(o, "ResolutionsAvailable").Select(ParseResolution).Where(r => r.width > 0 && r.height > 0).ToArray(),
					bitrateRange = ParseIntRange(Kid(o, "BitrateRange")),
					govLengthRange = gov,
					frameRates = Words(Attr(o, "FrameRatesSupported")).Select(w => ParseFloat(w, -1)).Where(f => f > 0).ToArray(),
					profiles = Words(Attr(o, "ProfilesSupported"))
				};
			}).ToArray();
		}

		/// <summary>
		/// Sends the configuration's XML back with the given values changed. Anything the camera
		/// returned that ODM doesn't edit (multicast, session timeout, extensions) is kept.
		/// </summary>
		public void SetVideoEncoderConfiguration(
			Media2VideoEncoderConfiguration cfg, Media2VideoEncoderOptions opts,
			VideoResolution resolution, float quality, float frameRate, int bitrate, int govLength
		) {
			var c = new XElement(cfg.xml);
			c.Name = tr2 + "Configuration";
			Func<XElement, string, XElement, XElement> ensure = (parent, name, after) => {
				var e = Kid(parent, name);
				if (e == null) {
					e = new XElement(tt + name);
					if (after != null) after.AddAfterSelf(e); else parent.AddFirst(e);
				}
				return e;
			};
			var inv = CultureInfo.InvariantCulture;

			var enc = ensure(c, "Encoding", Kid(c, "UseCount") ?? Kid(c, "Name"));
			enc.Value = opts.encodingName;

			var res = ensure(c, "Resolution", enc);
			ensure(res, "Width", null).Value = resolution.width.ToString(inv);
			ensure(res, "Height", Kid(res, "Width")).Value = resolution.height.ToString(inv);

			var rate = ensure(c, "RateControl", res);
			ensure(rate, "FrameRateLimit", null).Value = frameRate.ToString("0.###", inv);
			ensure(rate, "BitrateLimit", Kid(rate, "FrameRateLimit")).Value = bitrate.ToString(inv);

			// Quality goes after Multicast in the schema, so it's normally already there.
			var q = Kid(c, "Quality");
			if (q == null) {
				q = new XElement(tt + "Quality");
				(Kid(c, "Multicast") ?? rate).AddAfterSelf(q);
			}
			q.Value = quality.ToString("0.###", inv);

			// GovLength and Profile are encoding-specific attributes.
			var govAttr = c.Attributes().FirstOrDefault(a => a.Name.LocalName == "GovLength");
			if (opts.govLengthRange != null && govLength > 0) {
				c.SetAttributeValue(govAttr != null ? govAttr.Name : (XName)"GovLength", govLength.ToString(inv));
			} else if (govAttr != null && opts.encoding == VideoEncoding.jpeg) {
				govAttr.Remove();
			}
			var profAttr = c.Attributes().FirstOrDefault(a => a.Name.LocalName == "Profile");
			if (opts.profiles.Length > 0) {
				if (profAttr == null || !opts.profiles.Contains(profAttr.Value)) {
					c.SetAttributeValue(profAttr != null ? profAttr.Name : (XName)"Profile", opts.profiles[0]);
				}
			} else if (profAttr != null) {
				profAttr.Remove();
			}

			Call("SetVideoEncoderConfiguration", new XElement(tr2 + "SetVideoEncoderConfiguration", c));
		}
	}
}
