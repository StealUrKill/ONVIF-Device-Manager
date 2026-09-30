extern "C"{
#include "libavformat/avformat.h"
#include "libavcodec/avcodec.h"
}
#include <string>

// The FFmpeg calls are native code. Managed code must not use FFmpeg types directly, because some of
// them (for example AVDictionary) have no definition in the headers (linker warning LNK4248).
#pragma managed(push, off)
namespace{
	struct Mp4Native{
		AVFormatContext* oc;
		AVStream* stream;
		AVCodecID codecId;
		std::string path;
		int64_t firstPts;
		int64_t lastPts;
		bool headerWritten;
	};

	Mp4Native* Mp4Create(const char* path, bool h265, int* err){
		auto w = new Mp4Native();
		w->oc = nullptr;
		w->stream = nullptr;
		w->codecId = h265 ? AV_CODEC_ID_HEVC : AV_CODEC_ID_H264;
		w->path = path;
		w->firstPts = -1;
		w->lastPts = -1;
		w->headerWritten = false;
		*err = avformat_alloc_output_context2(&w->oc, nullptr, "mp4", path);
		if(*err < 0 || w->oc == nullptr){
			if(*err >= 0) *err = AVERROR(ENOMEM);
			delete w;
			return nullptr;
		}
		w->stream = avformat_new_stream(w->oc, nullptr);
		if(w->stream == nullptr){
			avformat_free_context(w->oc);
			delete w;
			*err = AVERROR(ENOMEM);
			return nullptr;
		}
		w->stream->time_base.num = 1;
		w->stream->time_base.den = 90000;
		w->stream->codecpar->codec_type = AVMEDIA_TYPE_VIDEO;
		w->stream->codecpar->codec_id = w->codecId;
		return w;
	}

	// Decodes the key frame one time to get the size of the video.
	void Mp4DecodeSize(Mp4Native* w, const uint8_t* data, int length){
		const AVCodec* codec = avcodec_find_decoder(w->codecId);
		AVCodecContext* dec = codec != nullptr ? avcodec_alloc_context3(codec) : nullptr;
		if(dec == nullptr){
			return;
		}
		AVPacket* pkt = av_packet_alloc();
		AVFrame* picture = av_frame_alloc();
		if(avcodec_open2(dec, codec, nullptr) >= 0 && av_new_packet(pkt, length) >= 0){
			memcpy(pkt->data, data, length);
			avcodec_send_packet(dec, pkt);
			avcodec_send_packet(dec, nullptr);
			if(avcodec_receive_frame(dec, picture) >= 0){
				w->stream->codecpar->width = picture->width;
				w->stream->codecpar->height = picture->height;
			}else if(dec->width > 0 && dec->height > 0){
				w->stream->codecpar->width = dec->width;
				w->stream->codecpar->height = dec->height;
			}
		}
		av_frame_free(&picture);
		av_packet_free(&pkt);
		avcodec_free_context(&dec);
	}

	// The size of the video and the parameter sets come from the first key frame.
	int Mp4WriteHeader(Mp4Native* w, const uint8_t* frame, int length){
		AVCodecParserContext* parser = av_parser_init(w->codecId);
		AVCodecContext* cctx = avcodec_alloc_context3(nullptr);
		if(parser != nullptr && cctx != nullptr){
			uint8_t* out = nullptr;
			int outSize = 0;
			av_parser_parse2(parser, cctx, &out, &outSize, frame, length, 0, 0, 0);
			// The parser keeps the frame until the next one starts. An empty call ends the frame.
			av_parser_parse2(parser, cctx, &out, &outSize, nullptr, 0, 0, 0, 0);
			w->stream->codecpar->width = parser->width > 0 ? parser->width : cctx->width;
			w->stream->codecpar->height = parser->height > 0 ? parser->height : cctx->height;
		}
		if(parser != nullptr) av_parser_close(parser);
		if(cctx != nullptr) avcodec_free_context(&cctx);
		if(w->stream->codecpar->width <= 0 || w->stream->codecpar->height <= 0){
			Mp4DecodeSize(w, frame, length);
		}
		if(w->stream->codecpar->width <= 0 || w->stream->codecpar->height <= 0){
			return AVERROR_INVALIDDATA;
		}
		// The parameter sets in Annex-B format. The MP4 muxer makes the avcC or hvcC box from them.
		w->stream->codecpar->extradata = static_cast<uint8_t*>(av_mallocz(length + AV_INPUT_BUFFER_PADDING_SIZE));
		memcpy(w->stream->codecpar->extradata, frame, length);
		w->stream->codecpar->extradata_size = length;
		int err = avio_open(&w->oc->pb, w->path.c_str(), AVIO_FLAG_WRITE);
		if(err < 0) return err;
		err = avformat_write_header(w->oc, nullptr);
		if(err < 0) return err;
		w->headerWritten = true;
		return 0;
	}

	int Mp4WriteFrame(Mp4Native* w, const uint8_t* data, int length, int64_t pts90k, bool keyFrame){
		if(!w->headerWritten){
			if(!keyFrame) return 0;
			int err = Mp4WriteHeader(w, data, length);
			if(err < 0) return err;
		}
		if(w->firstPts < 0) w->firstPts = pts90k;
		int64_t pts = pts90k - w->firstPts;
		// MP4 needs increasing timestamps. RTP from a camera can repeat a timestamp.
		if(pts <= w->lastPts) pts = w->lastPts + 1;
		w->lastPts = pts;
		AVPacket* pkt = av_packet_alloc();
		if(av_new_packet(pkt, length) < 0){
			av_packet_free(&pkt);
			return AVERROR(ENOMEM);
		}
		memcpy(pkt->data, data, length);
		pkt->stream_index = w->stream->index;
		AVRational rtp;
		rtp.num = 1;
		rtp.den = 90000;
		pkt->pts = av_rescale_q(pts, rtp, w->stream->time_base);
		pkt->dts = pkt->pts;
		if(keyFrame) pkt->flags |= AV_PKT_FLAG_KEY;
		int err = av_interleaved_write_frame(w->oc, pkt);
		av_packet_free(&pkt);
		return err;
	}

	void Mp4Close(Mp4Native* w){
		if(w->headerWritten) av_write_trailer(w->oc);
		if(w->oc->pb != nullptr) avio_closep(&w->oc->pb);
		avformat_free_context(w->oc);
		delete w;
	}

	void Mp4ErrorText(int err, char* buf, size_t size){
		av_strerror(err, buf, size);
	}
}
#pragma managed(pop)

using namespace System;
using namespace System::Text;
using namespace System::Runtime::InteropServices;

namespace odm{
namespace player{

	///<summary>Writes H.264 or H.265 access units (Annex-B, with start codes) to an MP4 file.
	///The first key frame must have the parameter sets. The timestamps are in 90 kHz units.</summary>
	public ref class Mp4Writer: IDisposable{
	public:
		Mp4Writer(String^ path, bool h265){
			auto bytes = Encoding::UTF8->GetBytes(path + L"\0");
			pin_ptr<Byte> p = &bytes[0];
			int err = 0;
			native = Mp4Create(reinterpret_cast<const char*>(p), h265, &err);
			if(native == nullptr){
				throw gcnew Exception("cannot create the MP4 file: " + ErrorText(err));
			}
		}

		///<summary>Writes one access unit. Frames before the first key frame are not written.</summary>
		void WriteFrame(array<Byte>^ data, Int64 pts90k, bool keyFrame){
			if(native == nullptr){
				throw gcnew ObjectDisposedException("Mp4Writer");
			}
			if(data->Length == 0){
				return;
			}
			pin_ptr<Byte> p = &data[0];
			int err = Mp4WriteFrame(native, p, data->Length, pts90k, keyFrame);
			if(err < 0){
				throw gcnew Exception("cannot write the MP4 file: " + ErrorText(err));
			}
		}

		///<summary>True after the first key frame. Without it the file has no video.</summary>
		property bool HasVideo{
			bool get(){ return native != nullptr && native->headerWritten; }
		}

		void Close(){
			if(native != nullptr){
				Mp4Close(native);
				native = nullptr;
			}
		}

		~Mp4Writer(){
			Close();
		}
		!Mp4Writer(){
			Close();
		}

	private:
		static String^ ErrorText(int err){
			char buf[AV_ERROR_MAX_STRING_SIZE] = {0};
			Mp4ErrorText(err, buf, sizeof(buf));
			return gcnew String(buf);
		}

		Mp4Native* native;
	};
}
}
