#pragma once

extern "C"{
#include "libavformat/avformat.h"
#include "libavcodec/avcodec.h"
#include "libswscale/swscale.h"
#include "libavutil/pixfmt.h"
}

#include <string>
#include <memory>
#include <functional>
#include <atomic>

namespace onvifmp{
	using namespace std;

	///<summary>where decoded frames go: a picture in shared memory, read by the UI</summary>
	class VideoBuffer{
	public:
		int width;
		int height;
		AVPixelFormat pixelFormat;
		int stride[4];
		uint8_t* scan0[4];
		uint8_t* signal;
	};

	enum StreamTransport{
		Udp,
		Tcp,
		Http
	};

	class MediaStreamInfo{
	public:
		const char* url;
		const char* userName;		//nullptr: none
		const char* password;
		StreamTransport transport;
	};

	///<summary></summary>
	class IDisposable{
	public:
		virtual void Dispose()=0;
	};

	class IPlaybackSession: public IDisposable{
	};

	class IPlaybackController{
	public:
		virtual bool Initialized(IPlaybackSession* session)=0;
		virtual void Shutdown()=0;
	};

	///<summary>receives each packet of the ONVIF metadata stream</summary>
	typedef function<void(void* buffer, int size, bool markerBit, int seqNum)> MetadataListener;
}
