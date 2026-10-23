# os-webrtc-janus

## This branch: the hardened module for wolfvoice (version 1.2.0)

**By Shadow Wolf and Lone Wolf, Wolf Territories Grid and IntelligentWolf Ltd.**

Branch `chatsession-p2p-session-id-and-fast-fail` of
[intelligentwolf/os-webrtc-janus](https://github.com/intelligentwolf/os-webrtc-janus) is
the region module that the [wolfvoice](https://github.com/intelligentwolf/wolfvoice)
spatial voice server is built and tested against. Compared with the archived upstream
addon it adds:

- **The text-IM fix**: `ChatSessionRequest` answers `start p2p voice` with the viewer's
  own session id and fails `start conference` fast instead of after 30 s. (The same
  fix is in upstream OpenSimulator as [opensim/opensim#69](https://github.com/opensim/opensim/pull/69).)
- **Private voice calls and group voice**, with a signed ticket per avatar per room.
- **A relay key and a credential key** (`RpcKeyFile`, `CredKeyFile`), re-read when they change.
- **Voice off is enforced** at the region: estate and parcel voice flags, parcel bans and
  access lists, and neighbour (child-agent) connections always get the estate room.
- **Room rights re-checked on every connect** (group membership and voice power,
  conference membership, the other party of a call).
- **Its own TLS-verifying client for the voice server** (chain and host name are always
  checked, whatever OpenSim's global `NoVerifyCertChain` says).
- **A position feed**: once a second, while anyone is in voice, the region tells
  wolfvoice where its avatars really are, so a modified viewer cannot listen in from
  elsewhere (`PositionFeed = false` turns it off).
- Optional Wolf Territories features that stay **off** unless configured: ad-hoc
  conferences and Wolf Chat voice rooms need a conference service
  (`ConferenceApiUrl` plus a sim secret); without one, "start conference" fails fast
  with the viewer's own message, as before.

It builds against OpenSimulator 0.9.3.0 (classic prebuild tree, .NET 8) as an
addon-module, and against current OpenSimulator master in place of the in-tree copy.
**Full install and configuration steps, with the log lines to expect, are in the
[wolfvoice README](https://github.com/intelligentwolf/wolfvoice#2-build-the-region-module-into-opensim).**

The Janus backend (`WebRtcJanusService`) is unchanged from upstream and still builds;
it has not been re-tested against a Janus gateway on this branch.

---


> [!NOTE]
> As of February 26, 2026, this code and functionality has been integrated
> into [OpenSimulator](http://opensimulator.org)
> and [NGC](https://github.com/OpenSim-NGC/OpenSim-Tranquillity)
> and therefore this `addon-module` will no longer be developed
> or updated.

Addon-module for [OpenSimulator] to provide webrtc voice support
using Janus-gateway.

For an explanation of background and architecture, 
this project was presented at the
[OpenSimulator Community Conference] 2024
in the presentation
[WebRTC Voice for OpenSimulator](https://www.youtube.com/watch?v=nL78fieIFYg).

This addon works by taking viewer requests for voice service and
using a separate, external [Janus-Gateway WebRTC server].
This can be configured to allow local region spatial voice
and grid-wide group and spatial voice. See the sections below.

For running that separate Janus server, check out
[os-webrtc-janus-docker] which has instructions for running
Janus-Gateway on Linux and Windows WSL using Docker.

Instructions for:

- [Building into OpenSimulator](#Building): Build OpenSimulator with WebRTC voice service
- [Configuring Simulator for Voice Services](#Configure_Simulator)
- [Configuring Robust Grid Service](#Configure_Robust)
- [Configure Standalone Region](#Configure_Standalone)
- [Managing Voice Service](#Managing_Voice) (console commands, etc)

**Note**: as of January 2024, this solution does not provide true spatial
voice service using Janus. There are people working on additions to Janus
to provide this but the existing solution provides only non-spatial
voice services using the `AudioBridge` Janus plugin. Additionally,
features like muting and individual avatar volume are not yet implemented.

<a id="Known_Issues"></a>
## Known Issues

- No spatial audio
- One can see your own "white dot" but you don't see other avatar's white dots
- No muting
- No individual volume control

And probably more found at [os-webrtc-janus issues](https://github.com/Misterblue/os-webrtc-janus/issues).

<a id="Building"></a>
## Building Plugin into OpenSimulator

`os-webrtc-janus` is integrated as a source build into [OpenSimulator].
It uses the [OpenSimulator] addon-module feature which makes the
build as easy as cloning the `os-webrtc-janus` sources into the
[OpenSimulator] source tree, running the build configuration script,
and then building OpenSimulator.

The steps are:

```
# Get the OpenSimulator sources
git clone git://opensimulator.org/git/opensim
cd opensim     # cd into the top level OpenSim directory

# Fetch the WebRtc addon
cd addon-modules
git clone https://github.com/Misterblue/os-webrtc-janus.git
cd ..

# Build the project files
./runprebuild.sh

# Compile OpenSimulator with the webrtc addon
./compile.sh

# Copy the INI file for webrtc into a config dir that is read at boot
mkdir bin/config
cp addon-modules/os-webrtc-janus/os-webrtc-janus.ini bin/config
```

These building steps create several `.dll` files for `os-webrtc-janus`
in `bin/WebRtc*.dll`. Some adventurous people have found that, rather
than building the [OpenSimulator] sources, you can just copy the `.dll`s
into an existing `/bin` directory. Just make sure the `WebRtc*.dll` files
were built on the same version of [OpenSimulator] you are running.

<a id="Configure_Simulator"></a>
## Configure a Region for Voice

The last step in [Building](#Building) copied `os-webrtc-janus.ini` into 
the `bin/config` directory. [OpenSimulator] reads all the `.ini` files
in that directory so this copy operation adds the configuration for `os-webrtc-janus`
and this is what needs to be configured for the simulator and region.

The sample `.ini` file has two sections: `[WebRtcVoice]` and `[JanusWebRtcVoice]`.
The `WebRtcVoice` section configures the what services the simulator uses
for WebRtc voice. The `[JanusWebRtcVoice]` section configures any connection
the simulator makes to the Janus server. The latter section is only updated
if this simulator is using a local Janus server for spatial voice.

The values for `SpatialVoiceService` and `NonSpatialVoiceService` point
either directly to a Janus service or to a Robust grid server that is providing
the grid voice service. Both these options are in the sample `os-webrtc-janus.ini`
file and the proper one should be uncommented.

The viewer makes requests for either spatial voice (used in the region and parcels)
or non-spatial voice (used for group chats or person-to-person voice conversations).
`os-webrtc-janus` allows these two types of voice connections to be handled by
different voice services. Thus there are two different configurations:

- all voice service is provided by the grid (both spatial and non-spatial point to a robust service), and
- the region simulator provides a local Janus server for region spatial voice while the grid service is used for group chats

#### Grid Only Voice Services

The most common configuration will be for a simulator that uses the grid supplied
voice services. For this configuration, `os-webrtc-janus.ini` would look like:

```
[WebRtcVoice]
    Enabled = true
    SpatialVoiceService = WebRtcVoice.dll:WebRtcVoiceServiceConnector
    NonSpatialVoiceService = WebRtcVoice.dll:WebRtcVoiceServiceConnector
    WebRtcVoiceServerURI = ${Const|PrivURL}:${Const|PrivatePort}
```

This directs both spatial and non-spatial voice to the grid service connector
and `WebRtcVoiceServerURI` points to the configured Robust grid service.

There is no need for a `[JanusWebRtcVoice]` section because all that is handled by the grid services.

#### Local Simulator Janus Service

In a grid setup, there might be a need for a single simulator/region to use its own
Janus server for either privacy or to off-load the grid voice service.
In this configuration, spatial voice is directed to the local Janus service
while the non-spatial voice goes to the grid services to allow grid wide group
chat and region independent person-to-person chat.

This is done with a `os-webrtc-janus.ini` that looks like:
```
[WebRtcVoice]
    Enabled = true
    SpatialVoiceService = WebRtcJanusService.dll:WebRtcJanusService
    NonSpatialVoiceService = WebRtcVoice.dll:WebRtcVoiceServiceConnector
    WebRtcVoiceServerURI = ${Const|PrivURL}:${Const|PrivatePort}
[JanusWebRtcVoice]
    JanusGatewayURI = http://janus.example.org:14223/voice
    APIToken = APITokenToNeverCheckIn
    JanusGatewayAdminURI = http://janus.example.org/admin
    AdminAPIToken = AdminAPITokenToNeverCheckIn
```

Notice that, since the simulator has its own Janus service, it must configure the
connection parameters to access that Janus service. The details of running and
configuring a Janus service is provided at [os-webrtc-janus-docker] but, the configuration
here needs to specify the URI to address the Janus server and the API keys
to allow this simulator access to its interfaces. The example above
contains just sample entries.

<a id="Configure_Robust"></a>
## Configure Robust Server for WebRTC Voice

For the grid services side, `os-webrtc-janus` is configured as an additional service
in the Robust OpenSimulator server. The additions to `Robust.ini` are:

```
...
[ServiceList]
    ...
    VoiceServiceConnector = "${Const|PrivatePort}/WebRtcVoice.dll:WebRtcVoiceServerConnector"
    ...

[WebRtcVoice]
    Enabled = true
    SpatialVoiceService = WebRtcJanusService.dll:WebRtcJanusService
    NonSpatialVoiceService = WebRtcJanusService.dll:WebRtcJanusService
[JanusWebRtcVoice]
    JanusGatewayURI = http://janus.example.org:14223/voice
    APIToken = APITokenToNeverCheckIn
    JanusGatewayAdminURI = http://janus.example.org/admin
    AdminAPIToken = AdminAPITokenToNeverCheckIn
...
```

This adds `VoiceServiceConnector` to the list of services presented by this Robust server
and adds the WebRtcVoice configuration that says to do both spatial and non-spatial voice
using the Janus server, and the configuration for the Janus server itself.

One can configure multiple Robust services to distribute the load of services
and a Robust server with only `VoiceServiceConnector` in its ServiceList is possible.

<a id="Configure_Standalone"></a>
## Configure Standalone Region

[OpenSimulator] can be run "standalone" where all the grid services and regions are
run in one simulator instance. Adding voice to this configuration is sometimes useful
for very private meetings or testing. For this configuration, a Janus server is set up
and the standalone simulator is configured to point all voice to that Janus server:

```
[WebRtcVoice]
    Enabled = true
    SpatialVoiceService = WebRtcJanusService.dll:WebRtcJanusService
    NonSpatialVoiceService = WebRtcJanusService.dll:WebRtcJanusService
    WebRtcVoiceServerURI = ${Const|PrivURL}:${Const|PrivatePort}
[JanusWebRtcVoice]
    JanusGatewayURI = http://janus.example.org:14223/voice
    APIToken = APITokenToNeverCheckIn
    JanusGatewayAdminURI = http://janus.example.org/admin
    AdminAPIToken = AdminAPITokenToNeverCheckIn
```

This directs both spatial and non-spatial voice to the Janus server
and configures the URI address of  the Janus server and the API access
keys for that server.

<a id="Managing_Voice"></a>
## Managing Voice (Console commands)

There are a few console commands for checking on and controlling the voice system.
The current list of commands for the simulator can be listed with the
console command `help webrtc`.

This is a growing section and will be added to over time.

**webrtc list sessions** -- not implemented

**janus info** -- list many details of the Janus-Gateway configuration. Very ugly, non-formated JSON.

**janus list rooms** -- list the rooms that have been allocated in the `AudioBridge` Janus plugin

[SecondLife WebRTC Voice]: https://wiki.secondlife.com/wiki/WebRTC_Voice
[OpenSimulator]: http://opensimulator.org
[OpenSimulator Community Conference]: https://conference.opensimulator.org
[os-webrtc-janus]: https://github.com/Misterblue/os-webrtc-janus
[Janus-Gateway WebRTC server]: https://janus.conf.meetecho.com/
[os-webrtc-janus-docker]: https://github.com/Misterblue/os-webrtc-janus-docker
