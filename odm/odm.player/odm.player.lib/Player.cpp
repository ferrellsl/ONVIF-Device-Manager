#include "odm.player.lib/all.h"
#include "odm.player.lib/PlaybackPolicy.hpp"
#include "odm.player.lib/PlayerLog.hpp"

#include <thread>
#include <cstdio>
#include <cstring>

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
		shownWidth = shownHeight = 0;
		openError = 0;
	}

	///<summary>The RTSP status behind an FFmpeg error from opening a stream (FFmpeg reports a
	///camera's error reply as the matching HTTP error), or 0 when the camera didn't answer with
	///one: it couldn't be reached, timed out, or the connection broke.</summary>
	static int RtspStatusOf(int error){
		switch(error){
			case AVERROR_HTTP_BAD_REQUEST: return 400;
			case AVERROR_HTTP_UNAUTHORIZED: return 401;
			case AVERROR_HTTP_FORBIDDEN: return 403;
			case AVERROR_HTTP_NOT_FOUND: return 404;
			case AVERROR_HTTP_OTHER_4XX: return 499;
			case AVERROR_HTTP_SERVER_ERROR: return 500;
			default: return 0;
		}
	}

	///<summary>An FFmpeg error as text, for the log</summary>
	static string ErrorText(int error){
		char text[AV_ERROR_MAX_STRING_SIZE] = {};
		if(error == AVERROR_EXIT){
			return "no answer in time";
		}
		av_strerror(error, text, sizeof(text));
		return text;
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
		openError = AVERROR(ENOMEM);
		packet = av_packet_alloc();
		formatContext = avformat_alloc_context();
		if(packet == nullptr || formatContext == nullptr){
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
			openError = result;
			return false;
		}
		openError = AVERROR_STREAM_NOT_FOUND;

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
		openError = AVERROR_DECODER_NOT_FOUND;
		if(videoStream >= 0 && !OpenDecoder()){
			return false;
		}
		openError = 0;
		return true;
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

		// What the network calls watch (FFmpeg asks several times a second whether to give up):
		// the view being closed, a deadline for opening the stream, and - while it plays - the
		// video stalling (the decisions are in PlaybackPolicy.hpp).
		struct Watch{
			PlaybackSession* session;
			ULONGLONG openDeadline;		//(0: not opening)
			bool reading;
			bool hasVideo;				//(this session has a video stream)
			bool videoInSession;		//(video has arrived in this session)
			bool videoArrived;			//(video has arrived at least once in this player, over all sessions)
			ULONGLONG lastVideo;		//(when video last arrived, or the session opened)
			bool stalled;
		}watch = {};
		watch.session = &playbackSession;
		AVIOInterruptCB interrupt = { [](void* data)->int{
			auto w = static_cast<Watch*>(data);
			if(w->session->disposed){
				return 1;
			}
			auto now = GetTickCount64();
			if(w->openDeadline != 0 && now > w->openDeadline){
				return 1;
			}
			if(w->reading && policy::Stalled(w->hasVideo, w->videoInSession, (int)((now - w->lastVideo) / 1000), w->videoArrived)){
				w->stalled = true;
				return 1;
			}
			return 0;
		}, &watch };
		const ULONGLONG openTime = 20000;		//(at most 20 s to open the stream)

		char camera[64];
		playerlog::CameraOf(mediaStreamInfo->url, camera, sizeof(camera));
		watch.openDeadline = GetTickCount64() + openTime;
		auto opened = Open(mediaStreamInfo, interrupt);
		watch.openDeadline = 0;
		if(!opened){
			playerlog::Write(camera, "could not open the stream: %s", ErrorText(openError).c_str());
			Cleanup();
			return;
		}
		playerlog::Write(camera, "stream opened");
		if(!playbackController->Initialized(&playbackSession)){
			Cleanup();
			return;
		}

		// When the stream stalls (no video for 10 s: the camera went away, or dropped the session)
		// or ends while the view is still open, it is opened again, after a growing delay - but not
		// once the camera has refused the credentials. ODM doesn't restart a player that has reported
		// its end, so it isn't told: the picture just carries on once the stream is back.
		int attempt = 0;				//(reopenings since video last arrived)
		// (for the log: the outage under way, from the last video packet to the next)
		ULONGLONG outageSince = 0;		//(0: none)
		int unanswered = 0;				//(reopen attempts the camera didn't answer at all)
		for(;;){
			watch.reading = true;
			watch.hasVideo = videoStream >= 0;
			watch.videoInSession = false;
			watch.lastVideo = GetTickCount64();
			watch.stalled = false;
			unsigned videoPackets = 0;
			int result = 0;
			while(!playbackSession.disposed){
				result = av_read_frame(formatContext, packet);
				if(result == AVERROR(EAGAIN)){
					continue;
				}
				if(result < 0){
					break;									//(the stream ended, stalled, or the camera went away)
				}
				if(packet->stream_index == videoStream){
					if(!watch.videoInSession){
						if(outageSince != 0){
							int seconds = (int)((GetTickCount64() - outageSince) / 1000);
							playerlog::Write(camera, "video back after %d s without video; %d reopen attempt(s), %d of them unanswered%s",
								seconds, attempt, unanswered,
								unanswered > 0 ? " (the camera was unreachable: likely restarting)" :
								attempt <= 1 ? " (the camera answered at once: a network or stream hiccup)" : "");
							outageSince = 0;
							unanswered = 0;
						}else{
							playerlog::Write(camera, "video arriving");
						}
					}
					watch.videoInSession = true;
					watch.videoArrived = true;
					watch.lastVideo = GetTickCount64();
					attempt = 0;			//(video is back: the next reopening starts the backoff over)
					++videoPackets;
					Decode(packet);
				}else if(packet->stream_index == metadataStream){
					metadataListener(packet->data, packet->size, true, metadataSeqNum++);
				}
				av_packet_unref(packet);
			}
			watch.reading = false;
			Cleanup();
			if(playbackSession.disposed){
				playerlog::Write(camera, "view closed");
				break;
			}
			int seconds = (int)((GetTickCount64() - watch.lastVideo) / 1000);
			if(outageSince == 0){
				outageSince = watch.videoArrived ? watch.lastVideo : GetTickCount64();
				if(watch.stalled){
					playerlog::Write(camera, "video stopped: no packets for %d s (%u in this session)", seconds, videoPackets);
				}else{
					playerlog::Write(camera, "the stream ended: %s (%u video packets in this session)", ErrorText(result).c_str(), videoPackets);
				}
			}else if(watch.stalled){
				playerlog::Write(camera, "the reopened session brought no video in %d s", seconds);
			}else{
				playerlog::Write(camera, "the reopened session ended: %s", ErrorText(result).c_str());
			}

			// open it again (the delay keeps growing over reopenings that bring no video; it starts
			// over once video arrives)
			bool reopened = false;
			while(!playbackSession.disposed && !reopened){
				++attempt;
				for(int i = 0; i < policy::ReconnectDelay(attempt) * 10 && !playbackSession.disposed; ++i){
					Sleep(100);
				}
				if(playbackSession.disposed){
					break;
				}
				watch.openDeadline = GetTickCount64() + openTime;
				reopened = Open(mediaStreamInfo, interrupt);
				watch.openDeadline = 0;
				if(reopened){
					playerlog::Write(camera, "reopen attempt %d: session opened", attempt);
					break;
				}
				auto error = openError;
				Cleanup();
				if(playbackSession.disposed){
					break;
				}
				auto status = RtspStatusOf(error);
				if(status == 0){
					++unanswered;
				}
				if(!policy::RetryAfter(status)){
					playerlog::Write(camera, "reopen attempt %d: the camera refused the credentials (%d): giving up", attempt, status);
					break;				//(the credentials were refused: don't keep trying them)
				}
				playerlog::Write(camera, "reopen attempt %d failed: %s; next in %d s", attempt, ErrorText(error).c_str(), policy::ReconnectDelay(attempt + 1));
			}
			if(!reopened){
				if(!playbackSession.disposed){
					playerlog::Write(camera, "stopped");
					playbackController->Shutdown();
				}else{
					playerlog::Write(camera, "view closed");
				}
				break;
			}
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
		if(frame->width != shownWidth || frame->height != shownHeight){
			Describe(frame);
		}
		scaleContext = sws_getCachedContext(scaleContext, frame->width, frame->height, (AVPixelFormat)frame->format,
			vb->width, vb->height, vb->pixelFormat, SWS_FAST_BILINEAR, nullptr, nullptr, nullptr);
		if(scaleContext == nullptr){
			return;
		}
		*vb->signal = 1;
		sws_scale(scaleContext, frame->data, frame->linesize, 0, frame->height, vb->scan0, vb->stride);
	}

	///<summary>tells the UI what is playing: the codec and the picture's own size</summary>
	void Player::Describe(const AVFrame* frame){
		auto vb = videoBuffer.get();
		shownWidth = frame->width;
		shownHeight = frame->height;
		if(vb->info == nullptr || vb->infoSize <= 0){
			return;
		}
		const char* codec;
		switch(codecContext->codec_id){
			case AV_CODEC_ID_HEVC: codec = "H.265"; break;
			case AV_CODEC_ID_H264: codec = "H.264"; break;
			case AV_CODEC_ID_MJPEG: codec = "MJPEG"; break;
			case AV_CODEC_ID_MPEG4: codec = "MPEG-4"; break;
			default: codec = codecContext->codec->name; break;
		}
		char text[64];
		snprintf(text, sizeof(text), "%s  %dx%d", codec, frame->width, frame->height);
		auto length = min((int)strlen(text), vb->infoSize - 1);
		vb->info[length] = 0;					//(cut short first: the UI may be reading it)
		memcpy(vb->info, text, length);
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
		shownWidth = shownHeight = 0;
	}
}
