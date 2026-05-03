// Shiny.Speech browser interop module for Web Speech API
let recognition = null;
let audioElement = null;
let recognitionStopped = false;

function getExports() {
    const { getAssemblyExports } = globalThis.getDotnetRuntime(0);
    return getAssemblyExports("Shiny.Speech");
}

export const shinySpeech = {
    // --- Speech Recognition (STT) ---
    isRecognitionSupported() {
        return !!(window.SpeechRecognition || window.webkitSpeechRecognition);
    },

    async requestMicrophoneAccess() {
        try {
            const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
            // Stop the tracks immediately — we just needed the permission grant
            stream.getTracks().forEach(t => t.stop());
            return true;
        } catch (e) {
            console.warn('[Shiny.Speech] Microphone access denied:', e.message);
            return false;
        }
    },

    async startRecognition(lang, continuous) {
        const SpeechRecognition = window.SpeechRecognition || window.webkitSpeechRecognition;
        if (!SpeechRecognition) {
            console.error('[Shiny.Speech] SpeechRecognition API not available');
            return;
        }

        recognitionStopped = false;

        function createAndStart() {
            if (recognitionStopped) return;

            recognition = new SpeechRecognition();
            recognition.continuous = continuous;
            recognition.interimResults = true;
            if (lang) recognition.lang = lang;

            recognition.onresult = (event) => {
                for (let i = event.resultIndex; i < event.results.length; i++) {
                    const result = event.results[i];
                    const text = result[0].transcript;
                    const isFinal = result.isFinal;
                    const confidence = result[0].confidence;

                    getExports().then(exports => {
                        exports.Shiny.Speech.BrowserSpeechToTextService.OnResult(text, isFinal, confidence);
                    });
                }
            };

            recognition.onend = () => {
                if (recognitionStopped) {
                    console.log('[Shiny.Speech] Recognition ended (stopped)');
                    getExports().then(exports => {
                        exports.Shiny.Speech.BrowserSpeechToTextService.OnEnd();
                    });
                } else if (continuous) {
                    console.log('[Shiny.Speech] Recognition ended, restarting (continuous mode)');
                    setTimeout(() => createAndStart(), 250);
                } else {
                    console.log('[Shiny.Speech] Recognition ended (single mode)');
                    getExports().then(exports => {
                        exports.Shiny.Speech.BrowserSpeechToTextService.OnEnd();
                    });
                }
            };

            recognition.onerror = (event) => {
                console.warn('[Shiny.Speech] Recognition error:', event.error);
                if (event.error === 'no-speech' || event.error === 'aborted') {
                    // Non-fatal: onend will fire and handle restart/end
                    return;
                }
                // Fatal errors
                recognitionStopped = true;
                getExports().then(exports => {
                    exports.Shiny.Speech.BrowserSpeechToTextService.OnError(event.error);
                });
            };

            try {
                recognition.start();
                console.log('[Shiny.Speech] Recognition started', { lang, continuous });
            } catch (e) {
                console.error('[Shiny.Speech] Failed to start recognition:', e.message);
                recognitionStopped = true;
                getExports().then(exports => {
                    exports.Shiny.Speech.BrowserSpeechToTextService.OnError(e.message);
                });
            }
        }

        createAndStart();
    },

    stopRecognition() {
        console.log('[Shiny.Speech] stopRecognition called');
        recognitionStopped = true;
        if (recognition) {
            recognition.stop();
            recognition = null;
        }
    },

    // --- Speech Synthesis (TTS) ---
    isSynthesisSupported() {
        return !!window.speechSynthesis;
    },

    getIsSpeaking() {
        return window.speechSynthesis?.speaking ?? false;
    },

    getVoices(cultureFilter) {
        if (!window.speechSynthesis) return "";

        const voices = window.speechSynthesis.getVoices();
        return voices
            .filter(v => !cultureFilter || v.lang.startsWith(cultureFilter))
            .map(v => `${v.voiceURI}|${v.name}|${v.lang}`)
            .join(";");
    },

    speak(text, lang, voiceUri, rate, pitch, volume) {
        if (!window.speechSynthesis) return;

        window.speechSynthesis.cancel();
        const utterance = new SpeechSynthesisUtterance(text);
        if (lang) utterance.lang = lang;
        utterance.rate = rate;
        utterance.pitch = pitch;
        utterance.volume = volume;

        if (voiceUri) {
            const voice = window.speechSynthesis.getVoices().find(v => v.voiceURI === voiceUri);
            if (voice) utterance.voice = voice;
        }

        utterance.onend = () => {
            getExports().then(exports => {
                exports.Shiny.Speech.BrowserTextToSpeechService.OnSpeakEnd();
            });
        };

        window.speechSynthesis.speak(utterance);
    },

    cancelSpeech() {
        window.speechSynthesis?.cancel();
    },

    // --- Audio Playback ---
    getIsPlaying() {
        return audioElement ? !audioElement.paused : false;
    },

    playAudio(dataUrl) {
        if (audioElement) {
            audioElement.pause();
            audioElement = null;
        }
        audioElement = new Audio(dataUrl);
        audioElement.play();
    },

    stopAudio() {
        if (audioElement) {
            audioElement.pause();
            audioElement.currentTime = 0;
            audioElement = null;
        }
    }
};
