#pragma once
#include "odm.player.lib/core.h"

namespace onvifmp{
	using namespace std;

	///<summary>
	///Plays a camera's RTSP stream with FFmpeg: receives it (over UDP, TCP or
	///tunneled through HTTP), decodes the video (H.265, H.264, MJPEG, MPEG-4)
	///into the video buffer, and passes the ONVIF metadata stream's packets on
	///</summary>
	class Player{
	public:
		Player();
		~Player();

		///<summary>Setup output for video playback</summary>
		///<param name="videoBuffer">video buffer proposed for rendering of decoded frames</param>
		void SetVideoOutput(shared_ptr<VideoBuffer> videoBuffer);

		///<summary>Setup output metadata stream</summary>
		///<param name="metadataListener">callback to receive metadata</param>
		void SetMetadataOutput(MetadataListener metadataListener);

		///<summary>plays the stream until the session is disposed or the stream ends</summary>
		void Run(MediaStreamInfo* mediaStreamInfo, IPlaybackController* playbackController);

	protected:
		shared_ptr<VideoBuffer> videoBuffer;
		MetadataListener metadataListener;

		AVFormatContext* formatContext;
		AVCodecContext* codecContext;
		AVFrame* frame;
		AVPacket* packet;
		SwsContext* scaleContext;
		int videoStream;
		int metadataStream;
		int metadataSeqNum;

		static string UrlWithCredentials(const MediaStreamInfo* mediaStreamInfo);
		bool Open(MediaStreamInfo* mediaStreamInfo, AVIOInterruptCB interrupt);
		bool OpenDecoder();
		void Decode(const AVPacket* packet);
		void Render(const AVFrame* frame);
		void Cleanup();
	};
}
