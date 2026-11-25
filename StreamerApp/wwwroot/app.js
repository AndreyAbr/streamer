// WebRTC & Live Fallback Viewer Client

(function () {
    // DOM Elements
    const joinScreen = document.getElementById('joinScreen');
    const streamScreen = document.getElementById('streamScreen');
    const joinForm = document.getElementById('joinForm');
    const roomCodeInput = document.getElementById('roomCodeInput');
    const nicknameInput = document.getElementById('nicknameInput');
    const btnJoin = document.getElementById('btnJoin');
    const joinAlert = document.getElementById('joinAlert');

    const remoteVideo = document.getElementById('remoteVideo');
    const streamCanvas = document.getElementById('streamCanvas');
    const canvasCtx = streamCanvas ? streamCanvas.getContext('2d') : null;
    const streamOverlay = document.getElementById('streamOverlay');
    const overlayTitle = document.getElementById('overlayTitle');
    const overlaySubtitle = document.getElementById('overlaySubtitle');

    const pingStat = document.getElementById('pingStat');
    const infoPing = document.getElementById('infoPing');
    const viewersCount = document.getElementById('viewersCount');
    const iceStatus = document.getElementById('iceStatus');

    // Controls
    const btnAudioToggle = document.getElementById('btnAudioToggle');
    const iconVolumeHigh = document.getElementById('iconVolumeHigh');
    const iconVolumeMuted = document.getElementById('iconVolumeMuted');
    const volumeSlider = document.getElementById('volumeSlider');
    const btnRaiseHand = document.getElementById('btnRaiseHand');
    const handBtnText = document.getElementById('handBtnText');
    const btnToggleChat = document.getElementById('btnToggleChat');
    const chatUnreadBadge = document.getElementById('chatUnreadBadge');
    const btnFullscreen = document.getElementById('btnFullscreen');
    const iconFullscreen = document.getElementById('iconFullscreen');
    const iconExitFullscreen = document.getElementById('iconExitFullscreen');
    const btnLeave = document.getElementById('btnLeave');

    // Drawer
    const sideDrawer = document.getElementById('sideDrawer');
    const btnCloseDrawer = document.getElementById('btnCloseDrawer');
    const drawerTabs = document.querySelectorAll('.drawer-tab');
    const tabPanes = document.querySelectorAll('.tab-pane');

    // Chat
    const chatMessages = document.getElementById('chatMessages');
    const chatForm = document.getElementById('chatForm');
    const chatInput = document.getElementById('chatInput');

    // Participants
    const participantsList = document.getElementById('participantsList');

    // State
    let hubConnection = null;
    let peerConnection = null;
    let isHandRaised = false;
    let unreadCount = 0;
    let isDrawerOpen = false;
    let currentNickname = "";
    let pingInterval = null;
    let lastWebRtcFrameTime = 0;
    const liveImage = new Image();
    const participantsMap = new Map();

    // Auto-fill room code from URL query param if present (?code=XXX-YYY)
    const urlParams = new URLSearchParams(window.location.search);
    const codeFromUrl = urlParams.get('code');
    if (codeFromUrl) {
        roomCodeInput.value = formatRoomCode(codeFromUrl);
    }

    // Auto-format room code with hyphen (000-000)
    roomCodeInput.addEventListener('input', function (e) {
        let val = e.target.value.replace(/[^0-9a-zA-Z]/g, '').toUpperCase();
        if (val.length > 3) {
            val = val.substring(0, 3) + '-' + val.substring(3, 6);
        }
        e.target.value = val;
    });

    function formatRoomCode(str) {
        let clean = str.replace(/[^0-9a-zA-Z]/g, '').toUpperCase();
        if (clean.length > 3) {
            return clean.substring(0, 3) + '-' + clean.substring(3, 6);
        }
        return clean;
    }

    function escapeHtml(text) {
        const div = document.createElement('div');
        div.textContent = text;
        return div.innerHTML;
    }

    // Return to main menu (join screen)
    function returnToJoinScreen(message) {
        if (peerConnection) {
            try { peerConnection.close(); } catch { }
            peerConnection = null;
        }

        if (remoteVideo && remoteVideo.srcObject) {
            try {
                remoteVideo.srcObject.getTracks().forEach(t => t.stop());
            } catch { }
            remoteVideo.srcObject = null;
        }

        if (pingInterval) {
            clearInterval(pingInterval);
            pingInterval = null;
        }

        if (hubConnection) {
            try { hubConnection.stop(); } catch { }
            hubConnection = null;
        }

        isHandRaised = false;
        btnRaiseHand.classList.remove('hand-raised');
        handBtnText.textContent = "Поднять руку";
        closeDrawer();

        streamScreen.classList.remove('active');
        streamScreen.classList.add('hidden');
        joinScreen.classList.remove('hidden');
        joinScreen.classList.add('active');

        if (message) {
            showAlert(message);
        }
    }

    // Join Form Submit
    joinForm.addEventListener('submit', async function (e) {
        e.preventDefault();
        const code = roomCodeInput.value.trim();
        const nickname = nicknameInput.value.trim() || "Гость";

        if (!code) {
            showAlert("Пожалуйста, введите код комнаты");
            return;
        }

        setJoinLoading(true);
        hideAlert();

        try {
            await initSignalR();
            const res = await hubConnection.invoke("JoinRoom", code, nickname);

            if (res.success) {
                currentNickname = nickname;
                joinScreen.classList.remove('active');
                joinScreen.classList.add('hidden');
                streamScreen.classList.remove('hidden');
                streamScreen.classList.add('active');

                // Load existing participants
                participantsMap.clear();
                if (res.viewers && Array.isArray(res.viewers)) {
                    res.viewers.forEach(v => {
                        participantsMap.set(v.connectionId, {
                            nickname: v.nickname,
                            isHandRaised: v.isHandRaised
                        });
                    });
                }
                renderParticipants();

                // Load chat history
                if (res.chatHistory && Array.isArray(res.chatHistory)) {
                    res.chatHistory.forEach(msg => {
                        appendChatMessage(msg.sender, msg.text, msg.timestamp, msg.isHost, false);
                    });
                }

                startPingLoop();

                if (res.isBroadcasting) {
                    if (res.isPaused) {
                        showOverlay("Трансляция на паузе", "Ведущий временно приостановил показ экрана");
                    } else {
                        hideOverlay();
                    }
                    await startWebRtc();
                } else {
                    showOverlay("Трансляция еще не началась", "Ожидание запуска экрана ведущим...");
                }
            } else {
                showAlert(res.error || "Не удалось подключиться к трансляции");
            }
        } catch (err) {
            console.error("Join error:", err);
            showAlert("Ошибка соединения с сервером: " + (err.message || err));
        } finally {
            setJoinLoading(false);
        }
    });

    function showAlert(msg) {
        joinAlert.textContent = msg;
        joinAlert.classList.remove('hidden');
    }

    function hideAlert() {
        joinAlert.classList.add('hidden');
    }

    function setJoinLoading(isLoading) {
        const text = btnJoin.querySelector('.btn-text');
        const spinner = btnJoin.querySelector('.btn-spinner');
        if (isLoading) {
            btnJoin.disabled = true;
            text.classList.add('hidden');
            spinner.classList.remove('hidden');
        } else {
            btnJoin.disabled = false;
            text.classList.remove('hidden');
            spinner.classList.add('hidden');
        }
    }

    // ==========================================
    // SignalR Initialization
    // ==========================================
    async function initSignalR() {
        if (hubConnection) {
            try { await hubConnection.stop(); } catch { }
        }

        hubConnection = new signalR.HubConnectionBuilder()
            .withUrl("/streamhub")
            .withAutomaticReconnect()
            .build();

        // SignalR Handlers
        hubConnection.on("ReceiveChatMessage", function (msg) {
            appendChatMessage(msg.sender, msg.text, msg.timestamp, msg.isHost);
        });

        hubConnection.on("UserHandRaised", function (connId, nickname, raised) {
            updateParticipantHand(connId, nickname, raised);
        });

        hubConnection.on("ViewerJoined", function (connId, nickname) {
            addParticipant(connId, nickname);
        });

        hubConnection.on("ViewerLeft", function (connId) {
            removeParticipant(connId);
        });

        hubConnection.on("ViewerKicked", function () {
            alert("Вы были отключены ведущим трансляции");
            returnToJoinScreen("Вы были отключены ведущим");
        });

        hubConnection.on("StreamStarted", async function () {
            hideOverlay();
            await startWebRtc();
        });

        hubConnection.on("StreamPaused", function (isPaused) {
            if (isPaused) {
                showOverlay("Трансляция на паузе", "Ведущий временно приостановил показ экрана");
            } else {
                hideOverlay();
            }
        });

        // Host stopped broadcast -> show overlay instead of kicking viewer
        hubConnection.on("StreamStopped", function () {
            if (peerConnection) {
                try { peerConnection.close(); } catch { }
                peerConnection = null;
            }
            if (remoteVideo && remoteVideo.srcObject) {
                try { remoteVideo.srcObject.getTracks().forEach(t => t.stop()); } catch { }
                remoteVideo.srcObject = null;
            }
            if (streamCanvas) {
                streamCanvas.classList.add('hidden');
            }
            showOverlay("Трансляция приостановлена", "Ведущий временно остановил показ экрана");
        });

        hubConnection.onclose(function () {
            returnToJoinScreen("Связь с сервером потеряна");
        });

        hubConnection.on("ReceiveIceCandidate", async function (candidate) {
            if (peerConnection && candidate && candidate.candidate) {
                try {
                    await peerConnection.addIceCandidate(new RTCIceCandidate(candidate));
                } catch (e) {
                    console.warn("Could not add ICE candidate:", e);
                }
            }
        });

        // Live Fallback Frame Stream (WebSocket JPEG stream)
        hubConnection.on("LiveFrame", function (base64Jpeg) {
            const now = Date.now();
            const isWebRtcHealthy = (now - lastWebRtcFrameTime < 2500) && !remoteVideo.paused && remoteVideo.readyState >= 2;

            if (!isWebRtcHealthy && canvasCtx) {
                hideOverlay();
                streamCanvas.classList.remove('hidden');
                remoteVideo.classList.add('hidden');

                liveImage.onload = function () {
                    if (streamCanvas.width !== liveImage.width || streamCanvas.height !== liveImage.height) {
                        streamCanvas.width = liveImage.width;
                        streamCanvas.height = liveImage.height;
                    }
                    canvasCtx.drawImage(liveImage, 0, 0);
                };
                liveImage.src = "data:image/jpeg;base64," + base64Jpeg;
            }
        });

        await hubConnection.start();
    }

    // ==========================================
    // WebRTC PeerConnection
    // ==========================================
    async function startWebRtc() {
        if (peerConnection) {
            try { peerConnection.close(); } catch { }
        }

        const config = {
            iceServers: [
                { urls: 'stun:stun.l.google.com:19302' },
                { urls: 'stun:stun1.l.google.com:19302' }
            ]
        };

        peerConnection = new RTCPeerConnection(config);

        // Expect incoming video and audio tracks
        peerConnection.addTransceiver('video', { direction: 'recvonly' });
        peerConnection.addTransceiver('audio', { direction: 'recvonly' });

        const remoteStream = new MediaStream();
        remoteVideo.srcObject = remoteStream;

        let hasInitializedAudio = false;
        peerConnection.ontrack = function (event) {
            console.log("WebRTC track received:", event.track.kind);
            remoteStream.addTrack(event.track);
            hideOverlay();

            if (!hasInitializedAudio) {
                remoteVideo.muted = true;
                updateAudioIcon();
                hasInitializedAudio = true;
            }

            const playPromise = remoteVideo.play();
            if (playPromise !== undefined) {
                playPromise.then(() => {
                    console.log("Remote video playing successfully");
                }).catch(err => {
                    console.warn("Video play error, will retry on click:", err);
                });
            }
        };

        remoteVideo.ontimeupdate = function () {
            lastWebRtcFrameTime = Date.now();
            if (streamCanvas) {
                streamCanvas.classList.add('hidden');
            }
            remoteVideo.classList.remove('hidden');
        };

        remoteVideo.onclick = function () {
            if (remoteVideo.paused) {
                remoteVideo.play();
            }
            if (remoteVideo.muted) {
                remoteVideo.muted = false;
                updateAudioIcon();
            }
        };

        peerConnection.onicecandidate = function (event) {
            if (event.candidate && hubConnection) {
                hubConnection.invoke("SendIceCandidate", {
                    candidate: event.candidate.candidate,
                    sdpMid: event.candidate.sdpMid,
                    sdpMLineIndex: event.candidate.sdpMLineIndex
                }).catch(err => console.warn("Failed to send candidate:", err));
            }
        };

        peerConnection.oniceconnectionstatechange = function () {
            if (iceStatus) {
                iceStatus.textContent = peerConnection.iceConnectionState;
            }
        };

        // Create Offer & Send to ASP.NET Core Server
        const offer = await peerConnection.createOffer({
            offerToReceiveVideo: true,
            offerToReceiveAudio: true
        });
        await peerConnection.setLocalDescription(offer);

        const answerSdp = await hubConnection.invoke("SendOffer", offer.sdp);
        await peerConnection.setRemoteDescription(new RTCSessionDescription({
            type: 'answer',
            sdp: answerSdp
        }));
    }

    function showOverlay(title, subtitle) {
        overlayTitle.textContent = title;
        overlaySubtitle.textContent = subtitle;
        streamOverlay.classList.remove('hidden');
    }

    function hideOverlay() {
        streamOverlay.classList.add('hidden');
    }

    // ==========================================
    // Latency Ping Loop
    // ==========================================
    function startPingLoop() {
        if (pingInterval) clearInterval(pingInterval);
        pingInterval = setInterval(async () => {
            if (hubConnection && hubConnection.state === signalR.HubConnectionState.Connected) {
                const start = Date.now();
                try {
                    await hubConnection.invoke("Ping", start);
                    const rtt = Math.max(1, Date.now() - start);
                    pingStat.textContent = `${rtt} ms`;
                    if (infoPing) infoPing.textContent = `${rtt} ms`;
                } catch { }
            }
        }, 2000);
    }

    // ==========================================
    // Audio Controls
    // ==========================================
    btnAudioToggle.addEventListener('click', function () {
        remoteVideo.muted = !remoteVideo.muted;
        updateAudioIcon();
    });

    volumeSlider.addEventListener('input', function (e) {
        const vol = parseFloat(e.target.value);
        remoteVideo.volume = vol;
        if (vol === 0) {
            remoteVideo.muted = true;
        } else if (remoteVideo.muted) {
            remoteVideo.muted = false;
        }
        updateAudioIcon();
    });

    function updateAudioIcon() {
        if (remoteVideo.muted || remoteVideo.volume === 0) {
            iconVolumeHigh.classList.add('hidden');
            iconVolumeMuted.classList.remove('hidden');
            btnAudioToggle.classList.add('active');
        } else {
            iconVolumeHigh.classList.remove('hidden');
            iconVolumeMuted.classList.add('hidden');
            btnAudioToggle.classList.remove('active');
        }
    }

    // ==========================================
    // Raise Hand
    // ==========================================
    btnRaiseHand.addEventListener('click', async function () {
        isHandRaised = !isHandRaised;
        if (isHandRaised) {
            btnRaiseHand.classList.add('hand-raised');
            handBtnText.textContent = "Рука поднята";
        } else {
            btnRaiseHand.classList.remove('hand-raised');
            handBtnText.textContent = "Поднять руку";
        }

        if (hubConnection) {
            try {
                await hubConnection.invoke("RaiseHand", isHandRaised);
            } catch (e) {
                console.warn("Raise hand error:", e);
            }
        }
    });

    // ==========================================
    // Drawer & Tabs
    // ==========================================
    btnToggleChat.addEventListener('click', function () {
        toggleDrawer('chatTab');
    });

    btnCloseDrawer.addEventListener('click', function () {
        closeDrawer();
    });

    drawerTabs.forEach(tab => {
        tab.addEventListener('click', function () {
            const targetId = this.dataset.tab;
            switchTab(targetId);
        });
    });

    function toggleDrawer(preferredTab) {
        isDrawerOpen = !isDrawerOpen;
        if (isDrawerOpen) {
            sideDrawer.classList.add('open');
            btnToggleChat.classList.add('active');
            unreadCount = 0;
            updateUnreadBadge();
            if (preferredTab) switchTab(preferredTab);
        } else {
            closeDrawer();
        }
    }

    function closeDrawer() {
        isDrawerOpen = false;
        sideDrawer.classList.remove('open');
        btnToggleChat.classList.remove('active');
        if (chatInput && document.activeElement === chatInput) {
            chatInput.blur();
        }
    }

    function switchTab(targetId) {
        drawerTabs.forEach(t => {
            if (t.dataset.tab === targetId) t.classList.add('active');
            else t.classList.remove('active');
        });

        tabPanes.forEach(pane => {
            if (pane.id === targetId) pane.classList.add('active');
            else pane.classList.remove('active');
        });

        if (targetId === 'chatTab') {
            unreadCount = 0;
            updateUnreadBadge();
            // Prevent horizontal viewport scroll jitter by delaying focus until animation settles
            setTimeout(() => {
                if (isDrawerOpen && chatInput && document.activeElement !== chatInput) {
                    try {
                        chatInput.focus({ preventScroll: true });
                    } catch {
                        chatInput.focus();
                    }
                }
            }, 360);
        }
    }

    function updateUnreadBadge() {
        if (unreadCount > 0) {
            chatUnreadBadge.textContent = unreadCount > 99 ? '99+' : unreadCount;
            chatUnreadBadge.classList.remove('hidden');
        } else {
            chatUnreadBadge.classList.add('hidden');
        }
    }

    // ==========================================
    // Chat System
    // ==========================================
    chatForm.addEventListener('submit', async function (e) {
        e.preventDefault();
        const text = chatInput.value.trim();
        if (!text || !hubConnection) return;

        try {
            await hubConnection.invoke("SendChatMessage", text);
            chatInput.value = "";
        } catch (err) {
            console.error("Failed to send message:", err);
        }
    });

    function appendChatMessage(sender, text, timestamp, isHost, incrementUnread = true) {
        const msgDiv = document.createElement('div');
        msgDiv.className = `chat-msg ${isHost ? 'host-msg' : ''}`;

        const header = document.createElement('div');
        header.className = 'msg-header';

        const senderSpan = document.createElement('span');
        senderSpan.className = 'msg-sender';
        if (isHost) {
            senderSpan.innerHTML = `<span class="host-badge">Ведущий</span> ` + escapeHtml(sender);
        } else {
            senderSpan.textContent = sender;
        }

        const timeSpan = document.createElement('span');
        timeSpan.className = 'msg-time';
        timeSpan.textContent = timestamp;

        header.appendChild(senderSpan);
        header.appendChild(timeSpan);

        const body = document.createElement('div');
        body.className = 'msg-body';
        body.textContent = text;

        msgDiv.appendChild(header);
        msgDiv.appendChild(body);

        chatMessages.appendChild(msgDiv);
        chatMessages.scrollTop = chatMessages.scrollHeight;

        if (!isDrawerOpen && incrementUnread) {
            unreadCount++;
            updateUnreadBadge();
        }
    }

    // ==========================================
    // Participants System
    // ==========================================
    function addParticipant(connId, nickname) {
        participantsMap.set(connId, { nickname, isHandRaised: false });
        renderParticipants();
    }

    function removeParticipant(connId) {
        participantsMap.delete(connId);
        renderParticipants();
    }

    function updateParticipantHand(connId, nickname, isRaised) {
        if (!participantsMap.has(connId)) {
            participantsMap.set(connId, { nickname, isHandRaised: isRaised });
        } else {
            const p = participantsMap.get(connId);
            p.isHandRaised = isRaised;
        }
        renderParticipants();
    }

    function renderParticipants() {
        const count = participantsMap.size + 1; // +1 for host
        viewersCount.textContent = count;

        // Keep the host item, recreate other items
        const hostItem = participantsList.querySelector('.host-item');
        participantsList.innerHTML = '';
        if (hostItem) participantsList.appendChild(hostItem);

        participantsMap.forEach((p, connId) => {
            const item = document.createElement('div');
            item.className = 'participant-item';

            const avatar = document.createElement('div');
            avatar.className = 'p-avatar';
            avatar.textContent = (p.nickname || 'З')[0].toUpperCase();

            const info = document.createElement('div');
            info.className = 'p-info';

            const name = document.createElement('div');
            name.className = 'p-name';
            name.textContent = p.nickname;

            const role = document.createElement('div');
            role.className = 'p-role';
            role.textContent = 'Зритель';

            info.appendChild(name);
            info.appendChild(role);

            item.appendChild(avatar);
            item.appendChild(info);

            if (p.isHandRaised) {
                const hand = document.createElement('div');
                hand.className = 'p-hand-icon';
                hand.innerHTML = `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" width="16" height="16">
                    <path d="M18 11V6a2 2 0 0 0-4 0v4"></path>
                    <path d="M14 10V4a2 2 0 0 0-4 0v7"></path>
                    <path d="M10 10.5V6a2 2 0 0 0-4 0v8"></path>
                    <path d="M6 14v-2a2 2 0 0 0-4 0v5a7 7 0 0 0 7 7h3a7 7 0 0 0 7-7v-6a2 2 0 0 0-4 0v3"></path>
                </svg>`;
                hand.title = 'Рука поднята!';
                item.appendChild(hand);
            }

            participantsList.appendChild(item);
        });
    }

    // ==========================================
    // Fullscreen & Controls Auto-Hide
    // ==========================================
    function isFullscreenActive() {
        return !!(document.fullscreenElement || document.webkitFullscreenElement || document.mozFullScreenElement || document.msFullscreenElement);
    }

    async function toggleFullscreen() {
        try {
            if (!isFullscreenActive()) {
                const target = streamScreen || document.documentElement;
                if (target.requestFullscreen) {
                    await target.requestFullscreen();
                } else if (target.webkitRequestFullscreen) {
                    await target.webkitRequestFullscreen();
                } else if (document.documentElement.requestFullscreen) {
                    await document.documentElement.requestFullscreen();
                }
            } else {
                if (document.exitFullscreen) {
                    await document.exitFullscreen();
                } else if (document.webkitExitFullscreen) {
                    await document.webkitExitFullscreen();
                }
            }
        } catch (e) {
            console.warn("Fullscreen toggle failed:", e);
        }
    }

    btnFullscreen.addEventListener('click', toggleFullscreen);

    let controlsHideTimeout = null;
    let isHoveringControls = false;

    function showControls() {
        streamScreen.classList.remove('controls-hidden');
    }

    function scheduleHideControls() {
        if (!isFullscreenActive()) return;
        if (controlsHideTimeout) clearTimeout(controlsHideTimeout);
        controlsHideTimeout = setTimeout(() => {
            if (isFullscreenActive() && !isDrawerOpen && !isHoveringControls) {
                streamScreen.classList.add('controls-hidden');
            }
        }, 3000);
    }

    function handleFullscreenChange() {
        const active = isFullscreenActive();
        if (active) {
            iconFullscreen.classList.add('hidden');
            iconExitFullscreen.classList.remove('hidden');
            streamScreen.classList.add('is-fullscreen');
            scheduleHideControls();
        } else {
            iconFullscreen.classList.remove('hidden');
            iconExitFullscreen.classList.add('hidden');
            streamScreen.classList.remove('is-fullscreen');
            showControls();
            if (controlsHideTimeout) {
                clearTimeout(controlsHideTimeout);
                controlsHideTimeout = null;
            }
        }
    }

    document.addEventListener('fullscreenchange', handleFullscreenChange);
    document.addEventListener('webkitfullscreenchange', handleFullscreenChange);

    const streamControls = document.querySelector('.stream-controls');
    if (streamControls) {
        streamControls.addEventListener('mouseenter', () => { isHoveringControls = true; showControls(); });
        streamControls.addEventListener('mouseleave', () => { isHoveringControls = false; scheduleHideControls(); });
    }
    if (sideDrawer) {
        sideDrawer.addEventListener('mouseenter', () => { isHoveringControls = true; showControls(); });
        sideDrawer.addEventListener('mouseleave', () => { isHoveringControls = false; scheduleHideControls(); });
    }

    streamScreen.addEventListener('mousemove', () => {
        showControls();
        scheduleHideControls();
    });

    const videoContainer = document.querySelector('.video-container');
    if (videoContainer) {
        videoContainer.addEventListener('dblclick', (e) => {
            if (e.target.closest('#streamOverlay') || e.target.closest('.stream-controls') || e.target.closest('.side-drawer')) return;
            toggleFullscreen();
        });
    }

    // Leave
    btnLeave.addEventListener('click', function () {
        if (confirm("Вы действительно хотите выйти из трансляции?")) {
            returnToJoinScreen();
        }
    });

})();
