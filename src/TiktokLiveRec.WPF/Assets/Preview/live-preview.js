(function () {
    const video = document.getElementById("player");
    const statusElement = document.getElementById("status");
    let hlsInstance = null;
    let flvInstance = null;
    let currentPayload = null;
    let connectTimeoutId = null;

    function postMessage(payload) {
        try {
            if (window.chrome && window.chrome.webview) {
                window.chrome.webview.postMessage(JSON.stringify(payload));
            }
        } catch {
        }
    }

    function showStatus(message, isError) {
        if (!message) {
            statusElement.textContent = "";
            statusElement.classList.add("hidden");
            statusElement.classList.remove("error");
            return;
        }

        statusElement.textContent = message;
        statusElement.classList.remove("hidden");
        statusElement.classList.toggle("error", !!isError);
    }

    function disposePlayers() {
        clearConnectTimeout();

        if (hlsInstance) {
            hlsInstance.destroy();
            hlsInstance = null;
        }

        if (flvInstance) {
            flvInstance.destroy();
            flvInstance = null;
        }

        video.pause();
        video.removeAttribute("src");
        video.load();
    }

    function clearConnectTimeout() {
        if (connectTimeoutId !== null) {
            window.clearTimeout(connectTimeoutId);
            connectTimeoutId = null;
        }
    }

    function armConnectTimeout() {
        clearConnectTimeout();
        connectTimeoutId = window.setTimeout(function () {
            const message = "直播流连接超时";
            showStatus(message, true);
            postPhase("error", message, "connect-timeout");
        }, 15000);
    }

    function postPhase(type, message, detail) {
        postMessage({
            type,
            message,
            detail: detail || ""
        });
    }

    function isValidStreamUrl(value) {
        if (typeof value !== "string" || value.trim().length === 0) {
            return false;
        }

        try {
            const url = new URL(value);
            return url.protocol === "http:" || url.protocol === "https:";
        } catch {
            return false;
        }
    }

    async function playNative(url) {
        postPhase("loading", "正在启动原生 HLS 播放...", "native-hls-play");
        video.src = url;
        await video.play();
    }

    async function startHls(url) {
        postPhase("loading", "正在请求 HLS manifest...", `hls:${url}`);
        if (window.Hls && window.Hls.isSupported()) {
            hlsInstance = new window.Hls({
                enableWorker: true,
                lowLatencyMode: true,
                backBufferLength: 90
            });
            hlsInstance.loadSource(url);
            hlsInstance.attachMedia(video);
            hlsInstance.on(window.Hls.Events.MANIFEST_PARSED, async function () {
                postPhase("loading", "HLS manifest 已加载，正在启动播放...", "hls:manifest-parsed");
                await video.play();
            });
            hlsInstance.on(window.Hls.Events.LEVEL_LOADED, function () {
                postPhase("loading", "HLS 媒体分片请求成功，等待出画...", "hls:level-loaded");
            });
            hlsInstance.on(window.Hls.Events.ERROR, function (_, data) {
                if (data) {
                    const details = data.details || "HLS 流加载失败";
                    const message = details === "manifestLoadError"
                        ? "HLS manifest 加载失败"
                        : details === "fragLoadError"
                            ? "HLS 媒体分片加载失败"
                            : `HLS 播放失败: ${details}`;
                    const diagnostic = `fatal=${!!data.fatal}; type=${data.type || ""}; details=${details}`;
                    if (data.fatal) {
                        clearConnectTimeout();
                        showStatus(message, true);
                        postPhase("error", message, diagnostic);
                    }
                }
            });
            return;
        }

        if (video.canPlayType("application/vnd.apple.mpegurl")) {
            await playNative(url);
            return;
        }

        throw new Error("当前环境不支持 HLS 预览");
    }

    async function startFlv(url) {
        if (!(window.mpegts && window.mpegts.getFeatureList().mseLivePlayback)) {
            throw new Error("当前环境不支持 FLV 预览");
        }

        postPhase("loading", "正在初始化 FLV 直播流...", `flv:${url}`);
        flvInstance = window.mpegts.createPlayer({
            type: "flv",
            isLive: true,
            url
        }, {
            enableWorker: true,
            liveBufferLatencyChasing: true,
            autoCleanupSourceBuffer: true
        });
        flvInstance.attachMediaElement(video);
        flvInstance.load();
        postPhase("loading", "FLV 已加载，正在启动播放...", "flv:loaded");
        flvInstance.on(window.mpegts.Events.ERROR, function (_, details) {
            const message = `FLV 播放失败: ${details || "初始化失败"}`;
            clearConnectTimeout();
            showStatus(message, true);
            postPhase("error", message, `flv:${details || "unknown"}`);
        });
        await video.play();
    }

    async function startPlayback(payload) {
        currentPayload = payload;
        disposePlayers();
        showStatus("正在连接直播流...", false);
        postPhase("loading", "正在连接直播流...", `${payload.streamKind || "unknown"}:${payload.streamUrl || ""}`);
        armConnectTimeout();

        if (!isValidStreamUrl(payload.streamUrl)) {
            throw new Error("直播流地址无效");
        }

        if (payload.streamKind === "hls") {
            await startHls(payload.streamUrl);
            return;
        }

        if (payload.streamKind === "flv") {
            await startFlv(payload.streamUrl);
            return;
        }

        throw new Error("不支持的直播流类型");
    }

    video.addEventListener("loadedmetadata", function () {
        postPhase("loading", "已获取视频元数据，等待出画...", `loadedmetadata:${video.videoWidth}x${video.videoHeight}`);
        showStatus("", false);
    });

    video.addEventListener("playing", function () {
        clearConnectTimeout();
        showStatus("", false);
        postPhase("playing", "", `playing:${video.videoWidth}x${video.videoHeight}`);
    });

    video.addEventListener("waiting", function () {
        showStatus("直播流缓冲中...", false);
        postPhase("waiting", "直播流缓冲中...", "video:waiting");
    });

    video.addEventListener("error", function () {
        clearConnectTimeout();
        const mediaError = video.error;
        const message = mediaError && mediaError.code === MediaError.MEDIA_ERR_NETWORK
            ? "直播流网络错误或被浏览器拦截"
            : "直播流播放失败";
        showStatus(message, true);
        postPhase("error", message, `mediaError:${mediaError ? mediaError.code : "unknown"}`);
    });

    window.previewHost = {
        isReady: true,
        loadStream: async function (payload) {
            try {
                await startPlayback(payload);
            } catch (error) {
                clearConnectTimeout();
                const message = error && error.message ? error.message : "直播流播放失败";
                showStatus(message, true);
                postPhase("error", message, "exception");
            }
        },
        stop: function () {
            currentPayload = null;
            disposePlayers();
            showStatus("", false);
        }
    };

    postMessage({ type: "ready", message: "" });
})();
