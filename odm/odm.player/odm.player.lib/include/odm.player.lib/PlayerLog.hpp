#pragma once

// A record of each stream's dropouts and reconnects, one line per event, in
// %LOCALAPPDATA%\Onvif Device Manager\logs\player.log (every player host process appends to it):
//   2026-10-24 21:03:05  192.168.2.58:554  video stopped: no packets for 10 s
// so that afterwards a camera's outages can be told apart: one that reboots stops answering for
// a while, a network hiccup gets video back on the first reconnect. No credentials are written.
// When the file passes 5 MB it becomes player.log.1 and a new one is started.

#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <stdarg.h>
#include <stdio.h>
#include <string.h>

namespace onvifmp{
	namespace playerlog{

		///<summary>The camera's host[:port] from a stream URL, without any user:password@.</summary>
		inline void CameraOf(const char* url, char* camera, size_t size){
			camera[0] = 0;
			if(url == nullptr){
				return;
			}
			const char* start = strstr(url, "://");
			start = start != nullptr ? start + 3 : url;
			const char* end = start;
			while(*end != 0 && *end != '/'){
				++end;
			}
			for(const char* at = start; at < end; ++at){
				if(*at == '@'){
					start = at + 1;		//(user:password@ dropped)
				}
			}
			size_t length = (size_t)(end - start);
			if(length > size - 1){
				length = size - 1;
			}
			memcpy(camera, start, length);
			camera[length] = 0;
		}

		inline void Write(const char* camera, const char* format, ...){
			wchar_t path[MAX_PATH + 64];
			DWORD length = GetEnvironmentVariableW(L"LOCALAPPDATA", path, MAX_PATH);
			if(length == 0 || length >= MAX_PATH){
				return;
			}
			wcscat_s(path, L"\\Onvif Device Manager");
			CreateDirectoryW(path, nullptr);
			wcscat_s(path, L"\\logs");
			CreateDirectoryW(path, nullptr);
			wcscat_s(path, L"\\player.log");

			WIN32_FILE_ATTRIBUTE_DATA attributes;
			if(GetFileAttributesExW(path, GetFileExInfoStandard, &attributes) &&
			   (attributes.nFileSizeHigh != 0 || attributes.nFileSizeLow > 5u * 1024 * 1024)){
				wchar_t old[MAX_PATH + 64];
				wcscpy_s(old, path);
				wcscat_s(old, L".1");
				MoveFileExW(path, old, MOVEFILE_REPLACE_EXISTING);
			}

			char line[600];
			SYSTEMTIME now;
			GetLocalTime(&now);
			int n = _snprintf_s(line, sizeof(line), _TRUNCATE, "%04d-%02d-%02d %02d:%02d:%02d  %-21s  ",
				now.wYear, now.wMonth, now.wDay, now.wHour, now.wMinute, now.wSecond, camera);
			if(n < 0){
				n = (int)strlen(line);
			}
			va_list args;
			va_start(args, format);
			int m = _vsnprintf_s(line + n, sizeof(line) - n - 2, _TRUNCATE, format, args);
			va_end(args);
			n = m < 0 ? (int)strlen(line) : n + m;
			line[n++] = '\r';
			line[n++] = '\n';

			// (FILE_APPEND_DATA: each line goes on the end in one write, whichever process writes it)
			HANDLE file = CreateFileW(path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
				nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
			if(file == INVALID_HANDLE_VALUE){
				return;
			}
			DWORD written;
			WriteFile(file, line, (DWORD)n, &written, nullptr);
			CloseHandle(file);
		}
	}
}
