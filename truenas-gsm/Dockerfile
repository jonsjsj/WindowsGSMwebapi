FROM node:18-alpine

# docker CLI lets us manage sibling containers via the socket
RUN apk add --no-cache docker-cli bash tar gzip

WORKDIR /app
COPY package.json .
RUN npm install --production
COPY server.js .
COPY game-templates.json .

EXPOSE 7876

ENV PORT=7876 \
    DATA_DIR=/data \
    DATA_ROOT=/gameservers \
    HOST_NAME=truenas-gsm \
    API_TOKEN=changeme

CMD ["node", "server.js"]
