Learn and understand how live tv tuners work in jellyfin, in particular the M3U tuner.

Learn and understand how when users request to view a live tv channel, via the jellyfin client e.g. androidTV, web, etc, how the jellyfin server processes that request.

Understand how the M3U streams are created and how they connect to TVHeadend or other http iptv providers that stream mpegts back to jellyfin, who then streams it back to the end users.

Understand how different requests for streams are handled, specific scenarios like if the stream is "direct"/passthrough, or "remuxed" (e.g. the mpegts stream is put into a different container like HLS) or transcoded where the mpegts stream is transcoded (both video and audio) to other formats like h264,h264,AC3 etc...

Understand how these live tv streams are handled for "DVR" recordings when directed by a user or a timer.

Document the findings, use cases and architecture for how "live tv" tuning and stream functions in an MD document such that it can be referred to during development cycles. This document should be updated as new findings and understandings are made.

Use those findings, then explore to understand the this design/feature goal: improve channel tuning speed for end users using jellyfin clients. The overall approach to consider is to maintain a pool of streaming connections to the IPTV source, like Tvheadend specifically, that are ready to serve clients if the same channel is tuned that was once tuned before.

Consider the implications if the streaming direct, remuxed or transcoded. But for a 1st pass POC approach, focus on direct streaming.

Take note that direct streaming may not use FFMPEG at all to send data to clients. And also note, spawning ffmpeg for any reason, like remux or transcoding, takes 5+ seconds to start because primarily ffmpeg needs to buffer the stream (e.g. hls segments) to encode/render it.

The goal is to have a pool of connections to the iptv source that are created when a user tunes a channel. If a user, which could be a totally different client and user, tune that same channel, it has already been collecting data from the iptv/tvheadend source and can immediately start streaming it to the client.

You have access to the jellyfin source code locally. There is also the documentation here: <https://jellyfin.org/docs/>
And clients here <https://jellyfin.org/downloads/clients> though for now androidtv is priority for a POC.
You may also have access to the jellyfin api specification: jellyfin-openapi-stable.json if it helpful in understanding.

After that, with all this context/findings in mind, review the recent implementation and development with the jellyfin-warmpool plugin along with the modifications to jellyfin server required to support it. Document a plan for changes.
