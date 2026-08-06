# Relay image for Fly.io.
#
# Built from the REPO ROOT rather than relay/, because server.js resolves
# STATIC_DIR as ../controller — a build context of relay/ alone produces an
# image that serves the WebSocket fine and 404s every page request, which is a
# confusing failure to debug from the outside.
#
# See .dockerignore: the Unity project is excluded by allowlist, not by naming
# every directory to skip. Library/ alone is gigabytes.

FROM node:22-alpine

WORKDIR /app

# Manifests first so editing the controller or the relay does not reinstall
# dependencies on every deploy.
COPY relay/package.json relay/package-lock.json ./relay/
RUN cd relay && npm ci --omit=dev

COPY relay ./relay
COPY controller ./controller

WORKDIR /app/relay

# Must match internal_port in fly.toml. server.js reads process.env.PORT.
ENV PORT=8080
EXPOSE 8080

CMD ["node", "server.js"]
