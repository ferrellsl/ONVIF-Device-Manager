#include "odm.player.lib/all.h"

#include <thread>

namespace onvifmp{
	using namespace std;

	Player::Player(){
		formatContext = nullptr;
		codecContext = nullptr;
		frame = nullptr;
		packet = nullptr;
		scaleContext = nullptr;
		videoStream = -1;
		metadataStream = -1;
		metadataSeqNum = 0;
	}

	Player::~Player(){
		Cleanup();
	}

	void Player::SetVideoOutput(shared_ptr<VideoBuffer> videoBuffer){
		this->videoBuffer = videoBuffer;
	}

	void Player::SetMetadataOutput(MetadataListener metadataListener){
		this->metadataListener = metadataListener;
	}

	///<summary>rtsp://user:password@host/...: the credentials percent-encoded (FFmpeg decodes them)</summary>
	string Player::UrlWithCredentials(const MediaStreamInfo* mediaStreamInfo){
		string url = mediaStreamInfo->url;
		if(mediaStreamInfo->userName == nullptr || mediaStreamInfo->userName[0] == 0){
			return url;
		}
		auto scheme = url.find("://");
		if(scheme == string::npos){
			return url;
		}
		auto hostStart = scheme + 3;
		auto pathStart = url.find('/', hostStart);
		auto at = url.rfind('@', pathStart == string::npos ? string::npos : pathStart);
		if(at != string::npos && at >= hostStart){
			return url;								//(the url has its own)
		}
		auto encode = [](const char* s){
			static const char hex[] = "0123456789ABCDEF";
			string out;
			for(; *s; ++s){
				unsigned char c = (unsigned char)*s;
				if(isalnum(c) || c == '-' || c == '.' || c == '_' || c == '~'){
					out += (char)c;
				}else{
					out += '%';
					out += hex[c >> 4];
					out += hex[c & 15];
				}
			}
			return out;
		};
		string credentials = encode(mediaStreamInfo->userName);
		if(mediaStreamInfo->password != nullptr){
			credentials += ":" + encode(mediaStreamInfo->password);
		}
		return url.substr(0, hostStart) + credentials + "@" + url.substr(hostStart);
	}

	bool Player::Open(MediaStreamInfo* mediaStreamInfo, AVIOInterruptCB interrupt){
		formatContext = avformat_alloc_context();
		if(formatContext == nullptr){
			return false;
		}
		formatContext->interrupt_callback = interrupt;
		formatContext->flags |= AVFMT_FLAG_NOBUFFER;

		AVDictionary* options = nullptr;
		auto transport = mediaStreamInfo->transport == StreamTransport::Udp ? "udp"
			: mediaStreamInfo->transport == StreamTransport::Tcp ? "tcp" : "http";
		av_dict_set(&options, "rtsp_transport", transport, 0);
		av_dict_set(&options, "timeout", "10000000", 0);			//(10 s without data: the camera is gone)
		av_dict_set(&options, "buffer_size", "5242880", 0);		//(UDP: room for big key frames, as before)
		av_dict_set(&options, "max_delay", "200000", 0);			//(UDP: wait at most 0.2 s for packets out of order)
		auto url = UrlWithCredentials(mediaStreamInfo);
		auto result = avformat_open_input(&formatContext, url.c_str(), nullptr, &options);
		av_dict_free(&options);
		if(result < 0){
			formatContext = nullptr;					//(freed by avformat_open_input)
			return false;
		}

		//the streams wanted: the first video stream that can be decoded, and the metadata stream
		for(unsigned i = 0; i < formatContext->nb_streams; ++i){
			auto stream = formatContext->streams[i];
			auto parameters = stream->codecpar;
			if(videoStream < 0 && videoBuffer != nullptr && parameters->codec_type == AVMEDIA_TYPE_VIDEO &&
				avcodec_find_decoder(parameters->codec_id) != nullptr){
				videoStream = (int)i;
			}else if(metadataStream < 0 && metadataListener != nullptr && parameters->codec_type == AVMEDIA_TYPE_DATA){
				metadataStream = (int)i;
			}else{
				stream->discard = AVDISCARD_ALL;
			}
		}
		if(videoStream < 0 && metadataStream < 0){
			return false;
		}
		return videoStream < 0 || OpenDecoder();
	}

	bool Player::OpenDecoder(){
		auto parameters = formatContext->streams[videoStream]->codecpar;
		auto codec = avcodec_find_decoder(parameters->codec_id);
		codecContext = avcodec_alloc_context3(codec);
		if(codecContext == nullptr || avcodec_parameters_to_context(codecContext, parameters) < 0){
			return false;
		}
		codecContext->flags |= AV_CODEC_FLAG_LOW_DELAY;
		//(a few threads: enough for 4K H.265, each one holding back a frame for live video)
		codecContext->thread_count = max(1, min(4, (int)thread::hardware_concurrency()));
		codecContext->thread_type = FF_THREAD_FRAME | FF_THREAD_SLICE;
		if(avcodec_open2(codecContext, codec, nullptr) < 0){
			return false;
		}
		frame = av_frame_alloc();
		return frame != nullptr;
	}

	void Player::Run(MediaStreamInfo* mediaStreamInfo, IPlaybackController* playbackController){
		struct PlaybackSession: IPlaybackSession{
			atomic<bool> disposed;
			PlaybackSession(){
				disposed = false;
			}
			virtual void Dispose(){
				disposed = true;
			}
		}playbackSession;

		//(blocking network calls give up as soon as the session is disposed)
		AVIOInterruptCB interrupt = { [](void* session)->int{
			return static_cast<PlaybackSession*>(session)->disposed ? 1 : 0;
		}, &playbackSession };

		packet = av_packet_alloc();
		if(packet == nullptr || !Open(mediaStreamInfo, interrupt)){
			Cleanup();
			return;
		}
		if(!playbackController->Initialized(&playbackSession)){
			Cleanup();
			return;
		}

		while(!playbackSession.disposed){
			auto result = av_read_frame(formatContext, packet);
			if(result == AVERROR(EAGAIN)){
				continue;
			}
			if(result < 0){
				break;									//(the stream ended, or the camera went away)
			}
			if(packet->stream_index == videoStream){
				Decode(packet);
			}else if(packet->stream_index == metadataStream){
				metadataListener(packet->data, packet->size, true, metadataSeqNum++);
			}
			av_packet_unref(packet);
		}
		if(!playbackSession.disposed){
			playbackController->Shutdown();
		}
		Cleanup();
	}

	void Player::Decode(const AVPacket* packet){
		if(avcodec_send_packet(codecContext, packet) < 0){
			return;										//(a damaged packet: the next key frame recovers)
		}
		while(avcodec_receive_frame(codecContext, frame) == 0){
			Render(frame);
			av_frame_unref(frame);
		}
	}

	///<summary>the frame, scaled to the video buffer and converted to its pixel format</summary>
	void Player::Render(const AVFrame* frame){
		auto vb = videoBuffer.get();
		scaleContext = sws_getCachedContext(scaleContext, frame->width, frame->height, (AVPixelFormat)frame->format,
			vb->width, vb->height, vb->pixelFormat, SWS_FAST_BILINEAR, nullptr, nullptr, nullptr);
		if(scaleContext == nullptr){
			return;
		}
		*vb->signal = 1;
		sws_scale(scaleContext, frame->data, frame->linesize, 0, frame->height, vb->scan0, vb->stride);
	}

	void Player::Cleanup(){
		if(scaleContext != nullptr){
			sws_freeContext(scaleContext);
			scaleContext = nullptr;
		}
		av_frame_free(&frame);
		av_packet_free(&packet);
		avcodec_free_context(&codecContext);
		if(formatContext != nullptr){
			avformat_close_input(&formatContext);
		}
		videoStream = -1;
		metadataStream = -1;
		metadataSeqNum = 0;
	}
}
