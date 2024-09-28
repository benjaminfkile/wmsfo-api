"use strict";
var __importDefault = (this && this.__importDefault) || function (mod) {
    return (mod && mod.__esModule) ? mod : { "default": mod };
};
Object.defineProperty(exports, "__esModule", { value: true });
const dotenv_1 = __importDefault(require("dotenv"));
const app_1 = __importDefault(require("./src/app"));
// Configuring dotenv
dotenv_1.default.config();
// Getting the PORT from environment variables
const PORT = process.env.PORT || 8000; // Default to 3000 if PORT is not provided in the environment
// Starting the server
app_1.default.listen(PORT, () => {
    console.log(`⚡️[server]: Server is running at http://localhost:${PORT}`);
});
