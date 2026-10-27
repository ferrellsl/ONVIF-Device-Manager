#pragma once

// The player's decisions about opening a stream again after it stalls, as plain functions.

namespace onvifmp{
	namespace policy{

		///<summary>Whether the stream has stalled and should be opened again, after 10 s without new
		///video packets. Only video counts (a metadata-only stream may be silent for a long time).
		///Before any video has arrived in this player, a stream whose packets never come (e.g. UDP
		///blocked) shows NO SIGNAL rather than being reopened over and over; once video has arrived at
		///least once (videoArrivedBefore, kept across reopenings), a reopened session that brings no
		///video is reopened again on the backoff schedule.</summary>
		inline bool Stalled(bool hasVideo, bool videoInThisSession, int secondsWithoutVideo, bool videoArrivedBefore){
			if(secondsWithoutVideo < 10){
				return false;
			}
			return videoArrivedBefore || (hasVideo && videoInThisSession);
		}

		///<summary>Seconds to wait before attempt number `attempt` (1, 2, ...) to open the stream
		///again: 2, 4, 8, 16, 32, then 60 s. The count goes back to 1 only once video arrives again,
		///not when a session merely opens.</summary>
		inline int ReconnectDelay(int attempt){
			int delay = 2;
			for(int i = 1; i < attempt && delay < 60; ++i){
				delay *= 2;
			}
			return delay > 60 ? 60 : delay;
		}

		///<summary>Whether to try again after an attempt to open the stream failed with the given
		///RTSP status (0 when there was none, e.g. the camera didn't answer). Not after 401: the
		///credentials are wrong, and repeated failed logins can lock the account on some cameras.</summary>
		inline bool RetryAfter(int rtspStatus){
			return rtspStatus != 401;
		}
	}
}
